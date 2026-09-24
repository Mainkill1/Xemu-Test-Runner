using System.Security.Cryptography;
using System.Text.Json;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Networking;

internal sealed partial class AgentJobStore
{
    public ActivityHub Activity { get; init; } = new();

    public AgentOperation? GetOperation(string id)
    {
        _ = ReadDocument(id);
        var path = OperationPath(id);
        if (!File.Exists(path)) return null;
        var operation = ReadJson<AgentOperation>(path);
        bool running;
        lock (_gate) { running = _runningOperations.Contains(id); }
        if (!running && operation.State is ("queued" or "running"))
        {
            // Queue location reconciles a crash after rename but before receipt.
            if (operation.Action == "submit" && Locate(id).State is ("queued" or "testing" or "tested"))
                return operation with { State = "completed", Result = new { job = Url(id), recoveredPublication = true } };
            return operation with
            {
                State = "interrupted",
                ErrorCode = "operation_interrupted",
                Error = "The runner restarted before this operation finished.",
                Hint = "Inspect the draft/file offsets. Validation and submission may be retried; cancel an interrupted clone and clone to a fresh ID."
            };
        }
        return operation;
    }

    public AgentOperation StartValidation(string id, bool submit, CancellationToken lifetime)
    {
        _ = ReadDocument(id);
        var action = submit ? "submit" : "validate";
        lock (_gate)
        {
            if (_runningOperations.Contains(id))
            {
                var operation = ReadJson<AgentOperation>(OperationPath(id));
                if (operation.Action == action) return operation;
                throw Conflict("job_busy", "Another operation is already running for this package.", "Poll the operation until it reaches a terminal state.");
            }
        }
        if (submit && Locate(id).State is ("queued" or "testing" or "tested"))
        {
            return GetOperation(id) ?? new AgentOperation(id, id, "submit", "completed", DateTimeOffset.UtcNow,
                Result: new { job = Url(id), alreadySubmitted = true });
        }

        var reservation = Reserve(id);
        try
        {
            _ = RequireDraft(id);
            return StartOperation(id, action, null, reservation, async (operation, ct) =>
            {
                var report = await ValidatePayloadAsync(id, operation, ct).ConfigureAwait(false);
                if (!report.Passed)
                    throw new AgentRequestException(422, "preflight_failed", "The package did not pass preflight.",
                        "GET the job's validation action for detailed checks. Correct the draft and validate again.");
                if (submit)
                {
                    ct.ThrowIfCancellationRequested();
                    var destination = System.IO.Path.Combine(_paths.Pending, "agent-" + id);
                    if (Directory.Exists(destination) || Locate(id).State != "draft")
                        throw Conflict("publication_conflict", "The package location changed before publication.",
                            "GET the job; use a new ID only when a separate attempt is intended.");
                    Directory.Move(Draft(id), destination);
                }
                return new { job = Url(id), submitted = submit, preflight = report };
            }, lifetime);
        }
        catch { reservation.Dispose(); throw; }
    }

    public AgentOperation Clone(string sourceId, string newId, CancellationToken lifetime)
    {
        _ = Home(newId);
        if (sourceId == newId) throw new InvalidDataException("Clone requires a new job ID.");
        if (File.Exists(System.IO.Path.Combine(Home(newId), "request.json")))
        {
            var existing = GetOperation(newId);
            if (existing?.Action == "clone" && existing.SourceJobId == sourceId) return existing;
            throw Conflict("job_identity_conflict", "The clone destination ID is already in use.", "Choose a fresh destination ID.");
        }

        var sourceLease = Reserve(sourceId);
        IDisposable? destinationLease = null;
        try
        {
            var sourceDocument = ReadDocument(sourceId);
            var location = Locate(sourceId);
            if (location.State is not ("draft" or "tested" or "cancelled"))
                throw Conflict("source_not_stable", "Only a draft, completed or cancelled package can be cloned.",
                    "Wait for the run to finish or withdraw an unclaimed queued job first.");
            var job = ReadJson<JobDefinition>(System.IO.Path.Combine(location.Package, "job.json"));
            job.Id = newId;
            Create(new AgentJobRequest(newId, job, sourceDocument.Request.Files));
            destinationLease = Reserve(newId);
            var ownership = new CombinedReservation(sourceLease, destinationLease);
            return StartOperation(newId, "clone", sourceId, ownership, async (operation, ct) =>
            {
                var count = 0;
                foreach (var file in sourceDocument.Request.Files)
                {
                    var source = ResolveFile(location.Package, file.Path);
                    var target = ResolveFile(Draft(newId), file.Path);
                    await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                        _bufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (input.Length != file.Length)
                        throw new InvalidDataException($"Source file length changed: {file.Path}.");
                    _ = await _uploads.ReceiveAsync(input, target, file.Length, null, file.Sha256, null,
                        _bufferBytes, ct).ConfigureAwait(false);
                    if (OperatingSystem.IsLinux() && file.Executable)
                        File.SetUnixFileMode(target, File.GetUnixFileMode(target) | UnixFileMode.UserExecute);
                    AtomicJson.Write(OperationPath(newId), operation with { State = "running", FilesChecked = ++count });
                }
                return new { job = Url(newId), sourceJob = Url(sourceId), next = "Edit the draft plan if needed, then submit." };
            }, lifetime);
        }
        catch
        {
            destinationLease?.Dispose();
            sourceLease.Dispose();
            throw;
        }
    }

    private AgentOperation StartOperation(string id, string action, string? source,
        IDisposable ownership, Func<AgentOperation, CancellationToken, Task<object?>> work,
        CancellationToken lifetime)
    {
        var operation = new AgentOperation(Guid.NewGuid().ToString("N"), id, action, "queued",
            DateTimeOffset.UtcNow, SourceJobId: source);
        lock (_gate) { _runningOperations.Add(id); }
        try { AtomicJson.Write(OperationPath(id), operation); }
        catch
        {
            lock (_gate) { _runningOperations.Remove(id); }
            ownership.Dispose();
            throw;
        }
        // Work belongs to the runner, not the requesting connection. Every task
        // observes its failure and persists a receipt; there is no fire-and-forget
        // exception or automatic second submission after a client disconnect.
        _ = Task.Run(async () =>
        {
            try
            {
                using var transfer = Activity.TrackTransfer(new { apiJob = id, action });
                AtomicJson.Write(OperationPath(id), operation with { State = "running" });
                var result = await work(operation, lifetime).ConfigureAwait(false);
                var last = ReadJson<AgentOperation>(OperationPath(id));
                AtomicJson.Write(OperationPath(id), last with
                {
                    State = "completed", FinishedUtc = DateTimeOffset.UtcNow, Result = result
                });
            }
            catch (Exception exception)
            {
                AgentOperation last;
                try { last = ReadJson<AgentOperation>(OperationPath(id)); }
                catch (Exception readError) when (readError is IOException or JsonException or UnauthorizedAccessException)
                { last = operation; }
                var failed = last with
                {
                    State = exception is OperationCanceledException ? "interrupted" : "failed",
                    FinishedUtc = DateTimeOffset.UtcNow,
                    ErrorCode = exception is AgentRequestException apiError ? apiError.Code :
                        exception is UploadFailure uploadError ? uploadError.Code : "package_operation_failed",
                    Error = exception.Message,
                    Hint = exception is AgentRequestException request ? request.Hint :
                        "Check the draft's declared file lengths/digests and upload status. Existing run evidence is unchanged."
                };
                // Publication is authoritative even if cancellation arrives after
                // rename. Never report a submitted package as safe to duplicate.
                if (action == "submit" && Locate(id).State is ("queued" or "testing" or "tested"))
                    failed = failed with { State = "completed", Error = null, ErrorCode = null, Hint = null, Result = new { job = Url(id), submitted = true } };
                try { AtomicJson.Write(OperationPath(id), failed); }
                catch (Exception writeError) { System.Diagnostics.Trace.TraceError("Cannot persist API operation: " + writeError); }
            }
            finally
            {
                lock (_gate) { _runningOperations.Remove(id); }
                ownership.Dispose();
            }
        });
        return operation;
    }

    private async Task<PreflightReport> ValidatePayloadAsync(string id, AgentOperation operation, CancellationToken ct)
    {
        var document = ReadDocument(id);
        var package = RequireDraft(id);
        var checks = new List<PreflightCheck>();
        var count = 0;
        foreach (var file in document.Request.Files)
        {
            ct.ThrowIfCancellationRequested();
            var path = ResolveFile(package, file.Path);
            var upload = _uploads.GetStatus(path);
            if (!upload.Complete || upload.Partial || upload.Length != file.Length)
            {
                checks.Add(new("payload", false, $"{file.Path}: not fully uploaded at the declared length."));
                continue;
            }
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                _bufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
            checks.Add(new("payload", hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase), file.Path));
            AtomicJson.Write(OperationPath(id), operation with { State = "running", FilesChecked = ++count });
        }
        var job = JobDefinition.LoadPackage(package);
        var declared = document.Request.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var required in new[] { job.Executable }.Concat(job.RequiredFiles)
                     .Concat(job.Inputs.Select(input => input.Path))
                     .Concat(job.RuntimeState.Files.Select(file => file.Source)))
        {
            var relative = System.IO.Path.GetRelativePath(package, JobDefinition.ResolveInsidePackage(package, required))
                .Replace(System.IO.Path.DirectorySeparatorChar, '/');
            checks.Add(new("declared_input", declared.Contains(relative), $"{required}: every package input must be in Files."));
        }
        var preflight = await Preflight.CheckAsync(job, package, _paths.Results, _preflight, ct).ConfigureAwait(false);
        checks.AddRange(preflight.Checks);
        var report = new PreflightReport(checks.All(check => check.Passed), preflight.ExecutableSha256, checks);
        AtomicJson.Write(System.IO.Path.Combine(Home(id), "validation.json"), report);
        AtomicJson.Write(OperationPath(id), operation with { State = "running", FilesChecked = count, Result = report });
        return report;
    }

    public PreflightReport? Validation(string id)
    {
        _ = ReadDocument(id);
        var path = System.IO.Path.Combine(Home(id), "validation.json");
        return File.Exists(path) ? ReadJson<PreflightReport>(path) : null;
    }

    private string OperationPath(string id) => System.IO.Path.Combine(Home(id), "operation.json");
    private sealed class CombinedReservation(IDisposable first, IDisposable second) : IDisposable
    {
        public void Dispose() { second.Dispose(); first.Dispose(); }
    }
}
