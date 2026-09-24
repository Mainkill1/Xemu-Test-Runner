using System.Security.Cryptography;
using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Networking;

/// <summary>
/// API-owned packages remain hidden until explicitly submitted. The queue and
/// this store compete using directory renames, never by editing a visible job.
/// A successful claim makes an API withdrawal fail, and vice versa.
/// </summary>
internal sealed partial class AgentJobStore
{
    private const int MaximumFiles = 4096;
    private const long MaximumStateBytes = 8 * 1024 * 1024;
    private readonly RunnerPaths _paths;
    private readonly FileUploadStore _uploads;
    private readonly PreflightOptions _preflight;
    private readonly int _bufferBytes;
    private readonly object _gate = new();
    private readonly HashSet<string> _busy = new(StringComparer.Ordinal);
    private readonly HashSet<string> _runningOperations = new(StringComparer.Ordinal);
    private string Root => System.IO.Path.Combine(_paths.Pending, ".agent-jobs");

    public AgentJobStore(RunnerPaths paths, FileUploadStore uploads, PreflightOptions preflight, int bufferBytes)
    {
        _paths = paths;
        _uploads = uploads;
        _preflight = preflight;
        _bufferBytes = bufferBytes;
    }

    public AgentJobView Create(AgentJobRequest request)
    {
        ValidateRequest(request);
        using var lease = Reserve(request.Id);
        var home = Home(request.Id);
        var creationHash = HashJson(request);
        var documentPath = System.IO.Path.Combine(home, "request.json");
        if (File.Exists(documentPath))
        {
            var existing = ReadDocument(request.Id);
            if (existing.CreationHash != creationHash)
            {
                throw Conflict("job_identity_conflict", "This job ID belongs to a different creation request.",
                    "Use a new ID for a new attempt, or use PUT plan with the current revision to edit a draft.");
            }
            return BuildView(request.Id, includeReservation: false);
        }

        Directory.CreateDirectory(home);
        var draft = Draft(request.Id);
        Directory.CreateDirectory(draft);
        // The payload need not exist yet. Use the runner's actual plan parser;
        // physical readiness and content hashes are checked by validate/submit.
        WritePlan(draft, request.Job);
        AtomicJson.Write(documentPath, new AgentJobDocument(request, creationHash, DateTimeOffset.UtcNow));
        return BuildView(request.Id, includeReservation: false);
    }

    public AgentJobView Get(string id) => BuildView(id, includeReservation: true);

    private AgentJobView BuildView(string id, bool includeReservation)
    {
        var document = ReadDocument(id);
        var (state, package) = Locate(id);
        var plan = File.Exists(System.IO.Path.Combine(package, "job.json"))
            ? ReadJson<JobDefinition>(System.IO.Path.Combine(package, "job.json"))
            : document.Request.Job;
        string? runId = null;
        try { runId = AttemptJournal.Read(package)?.RunId; }
        catch (Exception exception) when (exception is IOException or JsonException) { }

        var operation = GetOperation(id);
        bool busy;
        lock (_gate) { busy = includeReservation && _busy.Contains(id); }
        var baseUrl = Url(id);
        var actions = new Dictionary<string, AgentAction>
        {
            ["self"] = new("GET", baseUrl, "Read state and the current plan revision."),
            ["files"] = new("GET", baseUrl + "/files", "Inspect declarations and committed upload offsets."),
            ["validation"] = new("GET", baseUrl + "/validation", "Read the last structured validation report, or null before validation.")
        };
        if (operation is not null)
            actions["operation"] = new("GET", baseUrl + "/operation", "Poll the last operation; do not resubmit while running.");
        if (!busy && state == "draft")
        {
            actions["edit"] = new("PUT", baseUrl + "/plan", "Replace the draft plan with If-Match: revision.");
            actions["validate"] = new("POST", baseUrl + "/validate", "Check payload hashes and preflight without launching.");
            actions["submit"] = new("POST", baseUrl + "/submit", "Validate then atomically publish the whole package to Pending.");
        }
        if (!busy && state == "queued")
            actions["withdraw"] = new("POST", baseUrl + "/withdraw", "Return to draft only if the queue has not claimed it.");
        if (!busy && state is ("draft" or "queued"))
            actions["cancel"] = new("DELETE", baseUrl, "Cancel an unstarted job, retaining its files.");
        if (!busy && state is ("draft" or "tested" or "cancelled"))
            actions["clone"] = new("POST", baseUrl + "/clone", "Copy this package into a new draft ID for a separate attempt.");
        if (runId is not null)
        {
            var run = "/api/v1/runs/" + Uri.EscapeDataString(runId);
            actions["run"] = new("GET", run, "Read this attempt's result and artifact listing.");
            actions["result"] = new("GET", run + "/artifacts/result.json", "Download the completed result when available.");
            actions["stdout"] = new("GET", run + "/tail?file=stdout.log&bytes=32768", "Read a bounded log tail.");
        }
        return new(id, busy ? "busy" : state, Quote(HashJson(plan)), document.CreatedUtc,
            plan, document.Request.Files, runId, operation, actions);
    }

    public object List(int offset, int limit)
    {
        var ids = Directory.Exists(Root)
            ? Directory.EnumerateDirectories(Root).Select(System.IO.Path.GetFileName)
                .Where(id => id is not null && IsId(id) && File.Exists(System.IO.Path.Combine(Home(id), "request.json")))
                .OrderBy(id => id, StringComparer.Ordinal).Skip(offset).Take(limit + 1).ToArray()
            : [];
        // A page must not replicate up to 100 full multi-megabyte manifests.
        var items = ids.Take(limit).Select(id =>
        {
            var job = Get(id!);
            return new
            {
                job.Id, job.State, job.CreatedUtc, job.RunId,
                fileCount = job.Files.Count,
                operationState = job.Operation?.State,
                self = Url(job.Id)
            };
        }).ToArray();
        return new { items, nextOffset = ids.Length > limit ? (int?)(offset + limit) : null };
    }

    public object Files(string id)
    {
        var document = ReadDocument(id);
        var (_, package) = Locate(id);
        return document.Request.Files.Select(file => new
        {
            file.Path, file.Length, file.Sha256, file.Executable,
            status = _uploads.GetStatus(ResolveFile(package, file.Path)),
            upload = Url(id) + "/files/" + EncodePath(file.Path),
            statusUrl = Url(id) + "/files/" + EncodePath(file.Path) + "?upload-status=1"
        }).ToArray();
    }

    public UploadStatus FileStatus(string id, string relative)
    {
        var document = ReadDocument(id);
        _ = FindFile(document, relative);
        return _uploads.GetStatus(ResolveFile(Locate(id).Package, relative));
    }

    public async Task<UploadReceipt> UploadAsync(string id, string relative, Stream body,
        long length, UploadRange? range, string? requestedHash, string? uploadId, CancellationToken ct)
    {
        using var lease = Reserve(id);
        var document = ReadDocument(id);
        var package = RequireDraft(id);
        var file = FindFile(document, relative);
        if ((range?.Total ?? length) != file.Length)
            throw BadRequest("file_length_mismatch", "The upload length does not match its declaration.",
                "Send the declared whole-file length and sequential ranges, or create a new package.");
        if (requestedHash is not null && !requestedHash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw BadRequest("file_digest_mismatch", "The header digest differs from the package declaration.",
                "Use the declared whole-file digest, not a chunk digest.");

        var target = ResolveFile(package, relative);
        var receipt = await _uploads.ReceiveAsync(body, target, length, range, file.Sha256,
            uploadId, _bufferBytes, ct).ConfigureAwait(false);
        if (receipt.Complete && OperatingSystem.IsLinux() && file.Executable)
        {
            File.SetUnixFileMode(target, File.GetUnixFileMode(target) | UnixFileMode.UserExecute);
        }
        return receipt;
    }

    public AgentJobView ReplacePlan(string id, JobDefinition plan, string? revision)
    {
        using var lease = Reserve(id);
        _ = ReadDocument(id);
        var draft = RequireDraft(id);
        var current = ReadJson<JobDefinition>(System.IO.Path.Combine(draft, "job.json"));
        if (revision != Quote(HashJson(current)))
            throw new AgentRequestException(revision is null ? 428 : 412, "plan_revision_mismatch",
                "The plan revision is missing or has changed.",
                "GET this job and send its revision verbatim in If-Match; then retry the edit.");
        RequireJobId(id, plan);
        WritePlan(draft, plan);
        return BuildView(id, includeReservation: false);
    }

    public AgentJobView Withdraw(string id)
    {
        using var lease = Reserve(id);
        _ = ReadDocument(id);
        var location = Locate(id);
        if (location.State == "draft") return BuildView(id, includeReservation: false);
        if (location.State != "queued")
            throw Conflict("job_not_queued", "Only an unclaimed queued job can be withdrawn.",
                "Inspect the job state. Clone a completed job rather than rewriting its evidence.");
        try { Directory.Move(location.Package, Draft(id)); }
        catch (IOException exception)
        {
            throw Conflict("job_claim_race", exception.Message,
                "The queue may have claimed the package. GET the job before retrying; never edit Testing.");
        }
        return BuildView(id, includeReservation: false);
    }

    public AgentJobView Cancel(string id)
    {
        using var lease = Reserve(id);
        _ = ReadDocument(id);
        var location = Locate(id);
        if (location.State == "cancelled") return BuildView(id, includeReservation: false);
        if (location.State is not ("draft" or "queued"))
            throw Conflict("job_already_started", "An active or completed attempt cannot be deleted through cancellation.",
                "Use the target-control API to stop an active target. Evidence is immutable; clone for another attempt.");
        try { Directory.Move(location.Package, System.IO.Path.Combine(Home(id), "cancelled")); }
        catch (IOException exception)
        {
            throw Conflict("job_claim_race", exception.Message,
                "GET the job again; it may have been claimed before cancellation.");
        }
        return BuildView(id, includeReservation: false);
    }

    private void ValidateRequest(AgentJobRequest request)
    {
        if (request is null || !IsId(request.Id))
            throw BadRequest("job_id_invalid",
                "Id must be 1..64 lowercase letters, digits or hyphens, starting with a letter or digit.",
                "Choose a stable, unique ID and reuse it only when retrying the identical creation request.");
        RequireJobId(request.Id, request.Job);
        if (request.Files is null || request.Files.Count is < 1 or > MaximumFiles)
            throw BadRequest("files_invalid", $"Declare between 1 and {MaximumFiles} payload files.",
                "Include the executable and every dependency; job.json is sent as Job, not as a payload file.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in request.Files)
        {
            if (file is null) throw new InvalidDataException("Null file declaration.");
            ValidateRelative(file.Path);
            if (file.Path.Equals("job.json", StringComparison.OrdinalIgnoreCase) || !names.Add(file.Path))
                throw new InvalidDataException("Payload paths must be unique; job.json is reserved for the plan.");
            if (file.Length < 0 || file.Sha256 is null || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("Each file needs a non-negative 64-bit length and a complete SHA-256 digest.");
        }
        foreach (var name in names)
        {
            var parts = name.Split('/');
            for (var i = 1; i < parts.Length; i++)
                if (names.Contains(string.Join('/', parts.Take(i))))
                    throw new InvalidDataException("A declared file cannot also be a directory prefix.");
        }
    }

    private static void RequireJobId(string id, JobDefinition job)
    {
        if (job is null) throw new InvalidDataException("Job is required.");
        if (string.IsNullOrWhiteSpace(job.Id)) job.Id = id;
        if (job.Id != id) throw new InvalidDataException("Job.Id must equal the API job ID.");
    }

    private static void WritePlan(string package, JobDefinition job)
    {
        var temporary = System.IO.Path.Combine(package, ".agent-plan-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            AtomicJson.Write(temporary, job);
            _ = JobDefinition.LoadPackage(package, temporary);
            File.Move(temporary, System.IO.Path.Combine(package, "job.json"), overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private AgentJobDocument ReadDocument(string id)
    {
        var path = System.IO.Path.Combine(Home(id), "request.json");
        if (!File.Exists(path))
            throw new AgentRequestException(404, "job_not_found", "No API job has this ID.",
                "Create it with POST /api/v1/jobs, or list API jobs first.");
        return ReadJson<AgentJobDocument>(path);
    }

    private (string State, string Package) Locate(string id)
    {
        var name = "agent-" + id;
        var locations = new[]
        {
            (State: "draft", Package: Draft(id)),
            (State: "queued", Package: System.IO.Path.Combine(_paths.Pending, name)),
            (State: "testing", Package: System.IO.Path.Combine(_paths.Testing, name)),
            (State: "tested", Package: System.IO.Path.Combine(_paths.Tested, name)),
            (State: "cancelled", Package: System.IO.Path.Combine(Home(id), "cancelled"))
        };
        // Recheck if a concurrent rename crossed the first pass. Missing state
        // is never permission to publish a second copy of an attempt.
        for (var attempt = 0; attempt < 2; attempt++)
            foreach (var location in locations)
                if (Directory.Exists(location.Package)) return location;
        return ("unavailable", Draft(id));
    }

    private string RequireDraft(string id)
    {
        var location = Locate(id);
        if (location.State != "draft")
            throw Conflict("job_not_editable", "The package is not an editable draft.",
                "Withdraw an unclaimed queued job, or clone a completed job to a new ID. Never modify a running attempt.");
        return location.Package;
    }

    private string Home(string id)
    {
        if (!IsId(id)) throw new InvalidDataException("Invalid API job ID.");
        return System.IO.Path.Combine(Root, id);
    }
    private string Draft(string id) => System.IO.Path.Combine(Home(id), "payload");
    internal static string Url(string id) => "/api/v1/jobs/" + Uri.EscapeDataString(id);
    internal static string EncodePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
    private static bool IsId(string? id) => id is { Length: >= 1 and <= 64 } &&
        id[0] != '-' && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
    private static string Quote(string value) => "\"" + value + "\"";
    private static string HashJson(object value) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(value, ConfigLoader.JsonOptions))).ToLowerInvariant();

    private static T ReadJson<T>(string path)
    {
        // Allow atomic replacement on Windows while an agent polls. Readers
        // keep a stable handle to their document rather than blocking publication.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length > MaximumStateBytes) throw new InvalidDataException("API state document exceeds its size limit.");
        return JsonSerializer.Deserialize<T>(file, ConfigLoader.JsonOptions)
            ?? throw new InvalidDataException("Empty API state document.");
    }

    private static AgentFile FindFile(AgentJobDocument document, string relative) =>
        document.Request.Files.FirstOrDefault(file => file.Path == relative)
        ?? throw new AgentRequestException(404, "file_not_declared", "The file is not part of this package's manifest.",
            "Use a declared path, or create a new package with the intended file set.");

    private static void ValidateRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 2048 || relative.Contains('\\') ||
            relative.Contains(':') || relative.Contains('%') || System.IO.Path.IsPathRooted(relative))
            throw new InvalidDataException("Payload paths must be relative forward-slash paths.");
        foreach (var part in relative.Split('/'))
        {
            if (string.IsNullOrEmpty(part) || part.StartsWith('.') || part.EndsWith('.') || part != part.Trim() ||
                part.Any(c => char.IsControl(c) || "<>\"|?*".Contains(c)))
                throw new InvalidDataException("Payload paths cannot use empty, hidden, traversal or platform-reserved components.");
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
                (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '0' and <= '9'))
                throw new InvalidDataException("Payload path contains a reserved device name.");
        }
    }

    private static string ResolveFile(string root, string relative)
    {
        ValidateRelative(relative);
        var current = System.IO.Path.GetFullPath(root);
        if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked package directories are not supported.");
        foreach (var part in relative.Split('/'))
        {
            current = System.IO.Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked payload paths are not supported.");
        }
        return current;
    }

    private IDisposable Reserve(string id)
    {
        _ = Home(id);
        lock (_gate)
            if (!_busy.Add(id))
                throw Conflict("job_busy", "Another operation owns this package.",
                    "Poll the job/operation, then retry when its allowed actions are returned.");
        return new Reservation(this, id);
    }
    private sealed class Reservation(AgentJobStore owner, string id) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                lock (owner._gate) { owner._busy.Remove(id); }
        }
    }
    private static AgentRequestException Conflict(string code, string message, string hint) => new(409, code, message, hint);
    private static AgentRequestException BadRequest(string code, string message, string hint) => new(400, code, message, hint);
}
