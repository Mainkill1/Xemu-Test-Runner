using System.Text.Json;
using System.Text.Json.Serialization;
using XemuTestRunner.Config;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Networking;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record TestRunRequest(string Id, string ApplicationJobId, string TestId, string Revision);
internal sealed record RequestedTest(TestRunRequest Request, string Identity, string ApplicationIdentity,
    DateTimeOffset CreatedUtc, DateTimeOffset? StartRequestedUtc, string State,
    string? RunId = null, string? ErrorCode = null, string? Error = null);

internal sealed partial class AgentJobStore
{
    private readonly object _requestedGate = new();
    private readonly HashSet<string> _preparingRequests = new(StringComparer.Ordinal);
    private string RequestedRoot => System.IO.Path.Combine(_paths.Pending, ".requested-tests");
    private string RequestedPath(string id)
    {
        if (!IsId(id)) throw new InvalidDataException("Invalid test request ID.");
        RejectLinkedDirectory(RequestedRoot);
        return System.IO.Path.Combine(RequestedRoot, id + ".json");
    }

    public RequestedTest CreateRequestedTest(TestRunRequest request)
    {
        var path = RequestedPath(request.Id);
        if (request.Id == request.ApplicationJobId) throw new InvalidDataException("Test request and application need distinct IDs.");
        var baked = ReadTest(request.TestId, request.Revision);
        var app = ReadDocument(request.ApplicationJobId);
        RequireStableSource(Locate(request.ApplicationJobId).State);
        // Reject a misleading selection before it is saved or started. Recheck
        // during materialization as persisted requests may predate this guard.
        ValidateApplicationPayload(baked.Definition, app.Request);
        request = request with { Revision = request.Revision.ToLowerInvariant() };
        var identity = HashJson(request);
        lock (_requestedGate)
        {
            if (File.Exists(path))
            {
                var current = ReadRequestedTest(request.Id);
                if (current.Identity != identity || current.ApplicationIdentity != app.CreationHash)
                    throw Conflict("test_request_conflict", "The ID belongs to a different test/application request.", "Choose a new ID for a different attempt.");
                return current;
            }
            if (File.Exists(System.IO.Path.Combine(Home(request.Id), "request.json")))
                throw Conflict("test_request_conflict", "The ID is already used by an API job.", "Choose a new test request ID.");
            var created = new RequestedTest(request, identity, app.CreationHash, DateTimeOffset.UtcNow, null, "uploaded");
            AtomicJson.Write(path, created);
            return created;
        }
    }

    public RequestedTest ReadRequestedTest(string id)
    {
        var path = RequestedPath(id);
        if (!File.Exists(path)) throw new AgentRequestException(404, "test_request_not_found", "Unknown test request.", "List /api/v1/test-runs.");
        var value = ReadJson<RequestedTest>(path);
        if (value.Request is null || value.Request.Id != id || value.Identity != HashJson(value.Request))
            throw new InvalidDataException("Test request identity is invalid.");
        if (value.StartRequestedUtc is null || value.State == "cancelled") return value;
        var location = Locate(id);
        if (location.State is "queued" or "testing" or "tested")
        {
            var attempt = AttemptJournal.Read(location.Package);
            return value with
            {
                State = location.State == "queued" ? "queuedForExecution" : location.State == "testing" ?
                    attempt?.Phase == "held" ? "held" : "running" : "tested",
                RunId = attempt?.RunId,
                ErrorCode = null, Error = null
            };
        }
        return value;
    }

    public object RequestedView(RequestedTest value) => new
    {
        id = value.Request.Id, value.State, value.Request.TestId, value.Request.Revision,
        applicationJobId = value.Request.ApplicationJobId, value.RunId,
        startRequested = value.StartRequestedUtc is not null, value.CreatedUtc, value.StartRequestedUtc,
        value.ErrorCode, value.Error,
        application = "/api/v1/applications/" + Uri.EscapeDataString(value.Request.ApplicationJobId),
        job = File.Exists(System.IO.Path.Combine(Home(value.Request.Id), "request.json")) ? Url(value.Request.Id) + "?view=summary" : null,
        result = value.RunId is null ? null : "/api/v1/runs/" + Uri.EscapeDataString(value.RunId) + "?view=summary"
    };

    public object ListRequestedTests(int offset, int limit)
    {
        var values = ReadRequestedTests().OrderByDescending(item => item.CreatedUtc).Skip(offset).Take(limit + 1).ToArray();
        return new { items = values.Take(limit).Select(RequestedView).ToArray(), nextOffset = values.Length > limit ? (int?)(offset + limit) : null };
    }

    private IEnumerable<RequestedTest> ReadRequestedTests()
    {
        if (!Directory.Exists(RequestedRoot)) return [];
        RejectLinkedDirectory(RequestedRoot);
        return Directory.EnumerateFiles(RequestedRoot, "*.json").Select(path => ReadRequestedTest(System.IO.Path.GetFileNameWithoutExtension(path))).ToArray();
    }

    public RequestedTest RequestTestStart(string id)
    {
        lock (_requestedGate)
        {
            var current = ReadRequestedTest(id);
            if (current.State is "cancelled" or "failed")
                throw Conflict("test_request_terminal", "This request is cancelled or failed.", "Inspect its error; use a new ID for an intentional retry.");
            if (current.StartRequestedUtc is not null) return current;
            current = current with { StartRequestedUtc = DateTimeOffset.UtcNow, State = "queued" };
            AtomicJson.Write(RequestedPath(id), current);
            return current;
        }
    }

    public RequestedTest CancelRequestedTest(string id)
    {
        lock (_requestedGate)
        {
            var current = ReadRequestedTest(id);
            if (_preparingRequests.Contains(id) || current.State is "queuedForExecution" or "running" or "held" or "tested")
                throw Conflict("test_request_started", "Preparation/execution already owns this request.", "Use its existing job/control APIs; do not delete active state.");
            current = current with { State = "cancelled" };
            AtomicJson.Write(RequestedPath(id), current);
            return current;
        }
    }

    public async Task DispatchRequestedTestAsync(CancellationToken ct)
    {
        RequestedTest? next;
        lock (_requestedGate)
        {
            next = ReadRequestedTests().Where(value => value.StartRequestedUtc is not null && value.State is "queued" or "preparing" or "waitingForUpload")
                .OrderBy(value => value.StartRequestedUtc).ThenBy(value => value.Request.Id, StringComparer.Ordinal).FirstOrDefault();
            if (next is null || !_preparingRequests.Add(next.Request.Id)) return;
            AtomicJson.Write(RequestedPath(next.Request.Id), next with { State = "preparing" });
        }
        try
        {
            using var activity = Activity.TrackTransfer(new { action = "prepareRequestedTest", id = next.Request.Id });
            if (!await MaterializeRequestedTestAsync(next, ct).ConfigureAwait(false))
            {
                SaveRequestedState(next, "waitingForUpload", "application_incomplete", "Complete the application's declared uploads; the start request is retained.");
                return;
            }
            var operation = StartValidation(next.Request.Id, true, ct);
            while (operation.State is "queued" or "running")
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
                operation = GetOperation(next.Request.Id) ?? throw new InvalidDataException("Missing submission receipt.");
            }
            if (operation.State != "completed") throw new InvalidDataException(operation.Error ?? "Submission failed.");
            SaveRequestedState(next, "queuedForExecution");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException or AgentRequestException)
        {
            if (Locate(next.Request.Id).State is "queued" or "testing" or "tested") SaveRequestedState(next, "queuedForExecution");
            else SaveRequestedState(next, "failed", error is AgentRequestException request ? request.Code : "test_preparation_failed", error.Message);
        }
        finally { lock (_requestedGate) _preparingRequests.Remove(next.Request.Id); }
    }

    private void SaveRequestedState(RequestedTest request, string state, string? code = null, string? error = null)
    {
        lock (_requestedGate) AtomicJson.Write(RequestedPath(request.Request.Id), request with { State = state, ErrorCode = code, Error = error });
    }

    private async Task<bool> MaterializeRequestedTestAsync(RequestedTest request, CancellationToken ct)
    {
        var input = request.Request;
        var baked = ReadTest(input.TestId, input.Revision);
        var definition = baked.Definition;
        using var applicationLease = Reserve(input.ApplicationJobId);
        using var assetLease = definition.SourceJobId == input.ApplicationJobId ? null : Reserve(definition.SourceJobId);
        var application = ReadDocument(input.ApplicationJobId);
        if (application.CreationHash != request.ApplicationIdentity) throw new InvalidDataException("Application identity changed.");
        ValidateApplicationPayload(definition, application.Request);
        var applicationLocation = Locate(input.ApplicationJobId);
        var assetLocation = Locate(definition.SourceJobId);
        RequireStableSource(applicationLocation.State);
        RequireStableSource(assetLocation.State);
        var appExecutable = RelativeInput(applicationLocation.Package, application.Request.Job.Executable);
        var job = JsonSerializer.Deserialize<JobDefinition>(JsonSerializer.Serialize(definition.Job, ConfigLoader.JsonOptions), ConfigLoader.JsonOptions)!;
        job.Id = input.Id;
        job.Tags.RemoveAll(tag => tag.StartsWith("test-definition:", StringComparison.Ordinal));
        job.Tags.Add("test-definition:" + definition.Id + "@" + baked.Revision);
        var executable = RelativeInput(assetLocation.Package, job.Executable);
        var applicationFiles = application.Request.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var copies = definition.Files.Select(file =>
        {
            if (!definition.BuildFiles.Contains(file.Path, StringComparer.Ordinal)) return (File: file, Source: ResolveFile(assetLocation.Package, file.Path));
            var sourcePath = file.Path == executable ? appExecutable : file.Path;
            if (!applicationFiles.TryGetValue(sourcePath, out var replacement)) throw new InvalidDataException("Application is missing build slot: " + sourcePath);
            return (File: replacement with { Path = file.Path, Executable = file.Executable || replacement.Executable },
                Source: ResolveFile(applicationLocation.Package, sourcePath));
        }).ToArray();
        foreach (var copy in copies)
        {
            var status = _uploads.GetStatus(copy.Source);
            if (!status.Complete || status.Partial || status.Length != copy.File.Length) return false;
        }
        job.ExpectedExecutableSha256 = copies.Single(copy => copy.File.Path == executable).File.Sha256;
        foreach (var identity in job.Inputs)
        {
            var path = RelativeInput(assetLocation.Package, identity.Path);
            if (definition.BuildFiles.Contains(path, StringComparer.Ordinal)) identity.ExpectedSha256 = copies.Single(copy => copy.File.Path == path).File.Sha256;
        }
        PinEmulatorInput(job, copies.Select(copy => copy.File).ToArray());
        Create(new AgentJobRequest(input.Id, job, copies.Select(copy => copy.File).OrderBy(file => file.Path, StringComparer.Ordinal).ToArray()));
        if (Locate(input.Id).State != "draft") return true;
        using var targetLease = Reserve(input.Id);
        var target = RequireDraft(input.Id);
        foreach (var copy in copies)
        {
            ct.ThrowIfCancellationRequested();
            await using var source = new FileStream(copy.Source, FileMode.Open, FileAccess.Read, FileShare.Read,
                _bufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await _uploads.ReceiveAsync(source, ResolveFile(target, copy.File.Path), copy.File.Length, null, copy.File.Sha256, null, _bufferBytes, ct).ConfigureAwait(false);
            if (OperatingSystem.IsLinux() && copy.File.Executable)
            {
                var path = ResolveFile(target, copy.File.Path);
                File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);
            }
        }
        return true;
    }
}
