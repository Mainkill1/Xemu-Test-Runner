using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using XemuTestRunner.Config;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;
using XemuTestRunner.Runtime;

namespace XemuTestRunner.Networking;

internal sealed partial class AgentJobStore
{
    private BuildResultStore? _buildResults;
    public BuildResultStore BuildResults { get { lock (_gate) return _buildResults ??= new BuildResultStore(_paths.Results); } }

    public BuildRunRecord IndexBuildResult(string runId)
    {
        if (!BuildResultStore.SafeRunId(runId)) throw new InvalidDataException("Invalid run ID.");
        var catalog = new EvidenceCatalog(_paths.Results);
        var result = BuildResultStore.Read<JsonElement>(catalog.Resolve(runId, "result.json"));
        var jobId = Text(result, "job");
        if (Text(result, "runId") != runId) throw new InvalidDataException("Result identity does not match the run.");
        var location = Locate(jobId);
        var attempt = AttemptJournal.Read(location.Package);
        if (location.State != "tested" || attempt?.RunId != runId || attempt.Phase != "finalized")
            throw Conflict("run_not_archived", "Only a finalized archived API-owned attempt can be indexed.", "Wait for its current owner to archive it. Do not index a running or held result.");
        var source = ReadDocument(jobId);
        var job = JobDefinition.LoadPackage(location.Package);
        var sha = BuildResultStore.Sha(Text(result, "executableSha256"));
        var exe = RelativeInput(location.Package, job.Executable);
        var declaration = source.Request.Files.SingleOrDefault(file => file.Path == exe)
            ?? throw new InvalidDataException("Executable is missing from its immutable manifest.");
        if (!sha.Equals(declaration.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Result executable hash differs from the declared build.");
        var validation = Validation(jobId);
        var input = BuildResultStore.Read<JsonElement>(catalog.Resolve(runId, "input-manifest.json"));
        if (Text(input, "JobId") != jobId || !sha.Equals(Text(input, "ExecutableSha256"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Run input identity does not match the result.");
        var planBytes = File.ReadAllBytes(Path.Combine(location.Package, "job.json"));
        if (Convert.ToHexString(SHA256.HashData(planBytes)).ToLowerInvariant() != Text(input, "JobManifestSha256").ToLowerInvariant())
            throw new InvalidDataException("Archived job configuration differs from the executed configuration.");
        var assessment = new AgentAssessmentReader().Read(_paths.Results, runId);
        if (!assessment.Available || assessment.Outcome is null) throw new InvalidDataException("Cannot index an unavailable or malformed assessment: " + assessment.Code);
        var outcome = assessment.Outcome;
        var issues = new List<string>();
        if (validation?.Passed != true || !sha.Equals(validation.ExecutableSha256, StringComparison.OrdinalIgnoreCase)) issues.Add("payload_validation_missing");
        if (outcome.Execution != "completed" || outcome.Correctness != "passed" || outcome.Evidence != "complete" || outcome.Comparison != "eligible")
            issues.Add("assessment_ineligible");
        RunStorageReport? storage = null;
        if (job.RuntimeState.Isolation is not null)
        {
            try { storage = RunStateQualification.Read(catalog.Resolve(runId, ".")); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException) { issues.Add("state_evidence_invalid"); }
            if (storage?.ComparisonReady != true) issues.Add("state_not_qualified");
        }
        else if (job.Operations.IsBenchmark && File.Exists(catalog.Resolve(runId, "diagnostics/run-state/report.json")))
            issues.Add("benchmark_state_unmanaged");
        var metrics = new List<BuildMetric>();
        var keys = new HashSet<(string, string, string)>();
        if (result.TryGetProperty("workload", out var workload) && workload.TryGetProperty("Measurements", out var measurements) && measurements.ValueKind == JsonValueKind.Array)
        {
            // Guest suites expose several statistics per leaf. Keep a real
            // bound, but do not truncate a complete 149-record guest workload.
            if (measurements.GetArrayLength() > 4096) throw new InvalidDataException("Too many reported metrics (maximum 4096).");
            foreach (var item in measurements.EnumerateArray())
            {
                var name = Text(item, "Name"); var unit = Text(item, "Unit"); var direction = Text(item, "Direction");
                if (name.Length is < 1 or > 128 || unit.Length > 64 || direction is not ("lower" or "higher" or "neutral") ||
                    !item.TryGetProperty("Value", out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number) ||
                    !keys.Add((name, unit, direction)))
                { issues.Add("metric_invalid_or_duplicate"); continue; }
                metrics.Add(new(name, number, unit, direction));
            }
        }
        if (metrics.Count == 0) issues.Add("metrics_missing");
        if (outcome.Execution == "crashed") issues.Add("native_crash");
        var inventory = BuildResultStore.Read<JsonObject>(catalog.Resolve(runId, "host-inventory.json"));
        foreach (var name in new[] { "Machine", "OperatingSystem", "OsArchitecture", "ProcessArchitecture", "DotNet", "CpuModel", "LogicalProcessors", "GraphicsAdapters" })
            if (!inventory.ContainsKey(name)) throw new InvalidDataException("Environment identity is incomplete: " + name);
        inventory.Remove("CapturedUtc");
        var hasMonitoring = result.TryGetProperty("monitoring", out var monitoring) && monitoring.ValueKind == JsonValueKind.Object;
        if (!hasMonitoring) issues.Add("monitoring_unavailable");
        var environmentKey = HashJson(new { inventory, runnerVersion = Text(result.GetProperty("host"), "runnerVersion"),
            intervalMs = hasMonitoring && monitoring.TryGetProperty("intervalMs", out var interval) ? (int?)interval.GetInt32() : null,
            gpuProviders = hasMonitoring && monitoring.TryGetProperty("gpuProviders", out var providers) ? providers : JsonSerializer.SerializeToElement(Array.Empty<string>()) });
        var tag = job.Tags.SingleOrDefault(value => value.StartsWith("test-definition:", StringComparison.Ordinal));
        IReadOnlyList<string> buildPaths = [exe];
        var testLabel = job.Arguments.FirstOrDefault() ?? "custom";
        if (tag is not null)
        {
            var components = tag["test-definition:".Length..].Split('@');
            if (components.Length != 2) throw new InvalidDataException("Invalid test provenance.");
            buildPaths = ReadTest(components[0], components[1]).Definition.BuildFiles;
            testLabel = components[0];
        }
        var buildKey = HashJson(source.Request.Files.Where(file => buildPaths.Contains(file.Path, StringComparer.Ordinal))
            .Select(file => new { path = file.Path == exe ? "@executable" : file.Path, file.Length, sha256 = file.Sha256.ToLowerInvariant() })
            .OrderBy(file => file.path, StringComparer.Ordinal).ToArray());
        job.Id = "test";
        job.Tags.Clear();
        job.Executable = "@executable";
        job.ExpectedExecutableSha256 = null;
        job.RequiredFiles.RemoveAll(path => buildPaths.Contains(RelativeInput(location.Package, path), StringComparer.Ordinal));
        job.Inputs.RemoveAll(value => buildPaths.Contains(RelativeInput(location.Package, value.Path), StringComparer.Ordinal));
        job.Experiment.Id = null; job.Experiment.Variant = null; job.Experiment.Reference = null;
        job.Environment = job.Environment.OrderBy(item => item.Key, StringComparer.Ordinal).ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var testKey = HashJson(new { job, fixedFiles = source.Request.Files.Where(file => !buildPaths.Contains(file.Path, StringComparer.Ordinal))
            .OrderBy(file => file.Path, StringComparer.Ordinal).Select(file => new { file.Path, file.Length, sha256 = file.Sha256.ToLowerInvariant() }).ToArray() });
        testKey = RunStateQualification.ComparisonKey(testKey, storage, job.RuntimeState.Isolation is not null);
        var record = new BuildRunRecord(runId, sha, testKey, environmentKey, buildKey, testLabel, outcome, issues.Count == 0,
            issues.Distinct().ToArray(), metrics.OrderBy(metric => metric.Name, StringComparer.Ordinal).ThenBy(metric => metric.Unit, StringComparer.Ordinal).ToArray(),
            "/api/v1/runs/" + Uri.EscapeDataString(runId) + "/artifacts/metrics.csv");
        BuildResults.Save(record);
        return record;
    }
    private static string Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw new InvalidDataException("Required metadata field is missing: " + name);
}
