using System.Text.Json.Serialization;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Networking;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record TestConfigUpload(string SourceJobId, JobDefinition Job,
    string? Description = null, IReadOnlyList<string>? BuildFiles = null);

internal sealed partial class AgentJobStore
{
    // Author a new named definition without editing, cloning or executing its
    // source package. SourceJobId supplies retained assets, not the uploaded plan.
    public AgentTestSummary UploadConfig(string id, TestConfigUpload request)
    {
        var home = TestHome(id);
        if (request.Job is null) throw new InvalidDataException("Job is required.");
        if (request.Description?.Length > 240) throw new InvalidDataException("Description exceeds 240 characters.");
        using var sourceLease = Reserve(request.SourceJobId);
        var source = ReadDocument(request.SourceJobId);
        var location = Locate(request.SourceJobId);
        RequireStableSource(location.State);
        var job = request.Job;
        job.Id = id;
        Directory.CreateDirectory(TestRoot);
        var validation = System.IO.Path.Combine(TestRoot, ".validate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(validation);
        try
        {
            WritePlan(validation, job);
            job = JobDefinition.LoadPackage(validation);
        }
        finally { Directory.Delete(validation, true); }
        var executable = RelativeInput(location.Package, job.Executable);
        var files = source.Request.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var builds = (request.BuildFiles ?? new[] { executable }).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (builds.Length is < 1 or > 128 || builds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != builds.Length || !builds.Contains(executable))
            throw new InvalidDataException("BuildFiles needs 1..128 unique paths including the executable.");
        foreach (var path in builds)
        {
            ValidateRelative(path);
            if (!files.ContainsKey(path)) throw new InvalidDataException("Build slot is not in the source: " + path);
            if (job.RuntimeState.Files.Any(file => RelativeInput(location.Package, file.Source) == path))
                throw new InvalidDataException("Runtime seeds cannot be build replacement slots.");
        }
        foreach (var path in new[] { job.Executable }.Concat(job.RequiredFiles).Concat(job.Inputs.Select(file => file.Path))
                     .Concat(job.RuntimeState.Files.Select(file => file.Source)))
            if (!files.ContainsKey(RelativeInput(location.Package, path)))
                throw new InvalidDataException("The source manifest does not declare required input: " + path);
        var definition = new AgentTestDefinition(id, request.SourceJobId, request.Description ?? "", job,
            files.Values.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray(), builds);
        var revision = HashJson(definition);
        var baked = new AgentBakedTest(revision, definition, DateTimeOffset.UtcNow);
        var summary = TestSummary(baked);
        lock (_testLibraryGate)
        {
            Directory.CreateDirectory(home);
            var path = System.IO.Path.Combine(home, revision + ".json");
            if (File.Exists(path)) _ = ReadTest(id, revision);
            else AtomicJson.Write(path, baked);
            AtomicJson.Write(System.IO.Path.Combine(home, revision + ".summary.json"), summary);
        }
        return summary;
    }
}
