using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Networking;

internal sealed partial class AgentJobStore
{
    private readonly object _testLibraryGate = new();
    private string TestRoot => System.IO.Path.Combine(_paths.Pending, ".agent-tests");

    public AgentTestSummary BakeTest(string testId, AgentBakeRequest request)
    {
        var home = TestHome(testId);
        using var sourceLease = Reserve(request.SourceJobId);
        var source = ReadDocument(request.SourceJobId);
        var location = Locate(request.SourceJobId);
        RequireStableSource(location.State);
        var job = JobDefinition.LoadPackage(location.Package);
        var executable = RelativeInput(location.Package, job.Executable);
        var builds = (request.BuildFiles ?? new[] { executable }).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (builds.Length is < 1 or > 128 || builds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != builds.Length)
            throw BadRequest("test_build_files_invalid", "Declare between 1 and 128 distinct build files.", "Use package-relative executable/dependency paths.");
        foreach (var path in builds)
        {
            ValidateRelative(path);
            _ = FindFile(source, path);
            if (job.RuntimeState.Files.Any(file => RelativeInput(location.Package, file.Source) == path))
                throw BadRequest("test_seed_not_replaceable", "Mutable-state seeds cannot be build replacement slots.", "Bake a separate test revision for changed runtime seeds.");
        }
        if (!builds.Contains(executable, StringComparer.Ordinal))
            throw BadRequest("test_executable_slot_required", "BuildFiles must include the candidate executable.", "Include the executable plus any replaceable build dependencies.");
        if (request.Description is { Length: > 240 })
            throw new InvalidDataException("Test description must be at most 240 characters.");
        foreach (var file in source.Request.Files)
        {
            var status = _uploads.GetStatus(ResolveFile(location.Package, file.Path));
            if (!status.Complete || status.Partial || status.Length != file.Length)
                throw Conflict("test_source_incomplete", "The source package is not fully uploaded.", "Complete its uploads before baking. Payload hashes are rechecked when copied/submitted.");
        }
        var definition = new AgentTestDefinition(testId, request.SourceJobId, request.Description ?? "",
            job, source.Request.Files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray(), builds);
        var revision = HashJson(definition);
        var baked = new AgentBakedTest(revision, definition, DateTimeOffset.UtcNow);
        var summary = TestSummary(baked);
        lock (_testLibraryGate)
        {
            Directory.CreateDirectory(home);
            var path = System.IO.Path.Combine(home, revision + ".json");
            if (File.Exists(path))
            {
                _ = ReadTest(testId, revision); // Never silently replace an immutable definition.
            }
            else AtomicJson.Write(path, baked);
            AtomicJson.Write(System.IO.Path.Combine(home, revision + ".summary.json"), summary);
        }
        return summary;
    }

    public object ListTests(int offset, int limit)
    {
        if (!Directory.Exists(TestRoot)) return new { items = Array.Empty<AgentTestSummary>(), nextOffset = (int?)null };
        RejectLinkedDirectory(TestRoot);
        var paths = Directory.EnumerateDirectories(TestRoot)
            .Where(path => IsId(System.IO.Path.GetFileName(path)))
            .OrderBy(path => path, StringComparer.Ordinal)
            .SelectMany(path =>
            {
                RejectLinkedDirectory(path);
                return Directory.EnumerateFiles(path, "*.summary.json").OrderBy(file => file, StringComparer.Ordinal);
            }).Skip(offset).Take(limit + 1).ToArray();
        var items = paths.Take(limit).Select(path =>
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked test metadata is not supported.");
            return ReadJson<AgentTestSummary>(path);
        }).ToArray();
        return new { items, nextOffset = paths.Length > limit ? (int?)(offset + limit) : null };
    }

    public object DescribeTest(string id, string? revision, bool full)
    {
        var baked = ReadTest(id, revision);
        if (full) return baked;
        return new
        {
            summary = TestSummary(baked),
            buildFiles = baked.Definition.BuildFiles,
            create = "/api/v1/jobs/from-test",
            definition = TestUrl(id, baked.Revision) + "?view=definition"
        };
    }

    public AgentOperation PrepareTest(AgentTestRunRequest request, CancellationToken lifetime)
    {
        var baked = ReadTest(request.TestId, request.Revision);
        var definition = baked.Definition;
        var replacements = request.Files ?? Array.Empty<AgentFile>();
        if (replacements.Count > definition.BuildFiles.Count)
            throw BadRequest("test_file_not_replaceable", "Too many build replacements.", "Use only the baked test's buildFiles.");
        var files = definition.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var replacement in replacements)
        {
            if (replacement is null || !definition.BuildFiles.Contains(replacement.Path, StringComparer.Ordinal))
                throw BadRequest("test_file_not_replaceable", "The requested file is not a declared build replacement slot.", "Keep workload/config/seed inputs pinned; bake a new definition to change the test.");
            if (!seen.Add(replacement.Path)) throw new InvalidDataException("Duplicate build replacement.");
            var original = files[replacement.Path];
            files[replacement.Path] = replacement with { Executable = original.Executable || replacement.Executable };
        }
        // Deserialize a private copy. Neither later draft edits nor source plan
        // edits can change an already content-addressed test revision.
        var job = JsonSerializer.Deserialize<JobDefinition>(JsonSerializer.SerializeToUtf8Bytes(definition.Job, ConfigLoader.JsonOptions), ConfigLoader.JsonOptions)
            ?? throw new InvalidDataException("Stored test plan is empty.");
        job.Id = request.Id;
        job.Tags.RemoveAll(tag => tag.StartsWith("test-definition:", StringComparison.Ordinal));
        job.Tags.Add("test-definition:" + definition.Id + "@" + baked.Revision);
        var virtualPackage = Draft(request.Id);
        var executable = RelativeInput(virtualPackage, job.Executable);
        job.ExpectedExecutableSha256 = files[executable].Sha256;
        foreach (var input in job.Inputs)
        {
            var path = RelativeInput(virtualPackage, input.Path);
            if (definition.BuildFiles.Contains(path, StringComparer.Ordinal)) input.ExpectedSha256 = files[path].Sha256;
        }
        foreach (var label in new[] { request.ExperimentId, request.Variant, request.Reference })
            if (label is not null && (string.IsNullOrWhiteSpace(label) || label.Length > 128 || label.Any(char.IsControl)))
                throw new InvalidDataException("Experiment labels must contain 1..128 non-control characters.");
        if (request.ExperimentId is not null) job.Experiment.Id = request.ExperimentId;
        if (request.Variant is not null) job.Experiment.Variant = request.Variant;
        if (request.Reference is not null) job.Experiment.Reference = request.Reference;
        // Create's immutable creation hash pins the entire expanded request.
        // Lost responses reuse this ID; they never request another attempt.
        Create(new AgentJobRequest(request.Id, job, files.Values.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray()));
        return StartReuse(request.Id, definition.SourceJobId, lifetime);
    }

    private AgentBakedTest ReadTest(string id, string? revision)
    {
        var home = TestHome(id);
        if (string.IsNullOrWhiteSpace(revision))
            throw new AgentRequestException(428, "test_revision_required", "An immutable test revision is required.", "GET /api/v1/tests and pin the chosen revision; do not use an implicit latest version.");
        if (revision.Length != 64 || !revision.All(Uri.IsHexDigit)) throw new InvalidDataException("Test revision must be 64 hexadecimal characters.");
        revision = revision.ToLowerInvariant();
        var path = System.IO.Path.Combine(home, revision + ".json");
        if (!File.Exists(path))
            throw new AgentRequestException(404, "test_revision_not_found", "That test revision is not available.", "List tests and use an existing pinned ID/revision.");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked test definitions are not supported.");
        var baked = ReadJson<AgentBakedTest>(path);
        if (baked.Definition is null || baked.Definition.Id != id || baked.Revision != revision || HashJson(baked.Definition) != revision)
            throw new InvalidDataException("Stored test definition identity does not match its revision.");
        return baked;
    }

    private string TestHome(string id)
    {
        if (!IsId(id)) throw new InvalidDataException("Invalid test ID.");
        RejectLinkedDirectory(TestRoot);
        var home = System.IO.Path.Combine(TestRoot, id);
        RejectLinkedDirectory(home);
        return home;
    }
    private static void RejectLinkedDirectory(string path)
    {
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked test library directories are not supported.");
    }
    private static string RelativeInput(string package, string path) =>
        System.IO.Path.GetRelativePath(package, JobDefinition.ResolveInsidePackage(package, path)).Replace('\\', '/');
    private static void RequireStableSource(string state)
    {
        if (state is not ("draft" or "tested" or "cancelled"))
            throw Conflict("source_not_stable", "The source package is not stable.", "Use a draft, archived test or cancelled package; never copy a live attempt.");
    }
    private static string TestUrl(string id, string revision) => "/api/v1/tests/" + Uri.EscapeDataString(id) + "/" + revision;
    private static AgentTestSummary TestSummary(AgentBakedTest baked) => new(baked.Definition.Id, baked.Revision,
        baked.Definition.Description, baked.Definition.SourceJobId, baked.Definition.Job.Plan.Count,
        baked.Definition.Files.Count, baked.Definition.BuildFiles.Count, TestUrl(baked.Definition.Id, baked.Revision));
}
