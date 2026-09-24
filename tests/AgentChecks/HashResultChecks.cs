using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XemuTestRunner.Reliability;
using static AgentFixture;

internal static class HashResultChecks
{
    private static string A => Digest("baseline");
    private static string B => Digest("candidate");

    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("hash comparisons use matched server-side medians not executable names", async () =>
        {
            await using var host = new AgentFixture();
            await Archive(host, "a-one", "baseline", 100, executable: "old-name.exe");
            await Archive(host, "a-two", "baseline", 110, executable: "another.exe");
            await Archive(host, "b-one", "candidate", 80);
            await Archive(host, "b-two", "candidate", 90);
            var result = await host.Json($"/api/v1/compare?A={A}&B={B}");
            var row = result.GetProperty("rows")[0];
            Require(result.GetProperty("rows").GetArrayLength() == 1, "Executable names split identical workloads.");
            Require(row.GetProperty("a").GetDouble() == 105 && row.GetProperty("b").GetDouble() == 85, "Comparison did not use per-attempt medians.");
            Require(Math.Abs(row.GetProperty("changePercent").GetDouble() - (-19.047619)) < 0.001, "Wrong percent change.");
            Require(row.GetProperty("verdict").GetString() == "improved", "Lower runtime was not recognized.");
        }));
        checks.Add(("baseline is explicit and pinned results do not drift when new runs arrive", async () =>
        {
            await using var host = new AgentFixture();
            await Archive(host, "known", "baseline", 100);
            await Archive(host, "candidate", "candidate", 80);
            await host.Json($"/api/v1/compare?B={B}", expected: HttpStatusCode.Conflict);
            var pin = await host.Json("/api/v1/baseline", HttpMethod.Put, new { sha256 = A });
            Require(pin.GetProperty("runCount").GetInt32() == 1, "Baseline did not pin its run set.");
            await Archive(host, "later", "baseline", 500);
            var compared = await host.Json($"/api/v1/compare?B={B}");
            Require(compared.GetProperty("baselinePinned").GetBoolean(), "Default reference is not pinned.");
            Require(compared.GetProperty("rows")[0].GetProperty("a").GetDouble() == 100, "New baseline-hash run silently changed known results.");
            var report = await host.Json("/api/v1/build-results/" + B);
            Require(report.GetProperty("comparison").GetProperty("baselinePinned").GetBoolean(), "Build results do not compare to the default baseline.");
            Require(File.Exists(Path.Combine(host.Paths.Results, ".build-results", "baseline.json")), "Baseline is not persisted locally.");
        }));
        checks.Add(("ineligible runs cannot be hidden behind a fast successful repetition", async () =>
        {
            await using var host = new AgentFixture();
            await Archive(host, "known", "baseline", 100);
            await Archive(host, "fast", "candidate", 10);
            await Archive(host, "failed", "candidate", 1, eligible: false);
            var result = await host.Json($"/api/v1/compare?A={A}&B={B}");
            Require(result.GetProperty("blockedRuns").GetInt32() == 1, "Failed repetition disappeared.");
            Require(result.GetProperty("rows")[0].GetProperty("changePercent").ValueKind == JsonValueKind.Null, "Invalid cohort produced a speedup claim.");
            await host.Json("/api/v1/baseline", HttpMethod.Put, new { sha256 = B }, HttpStatusCode.Conflict);
        }));
        checks.Add(("different environments and workloads never produce relative performance claims", async () =>
        {
            await using var host = new AgentFixture();
            await Archive(host, "a", "baseline", 100, machine: "rig-one");
            await Archive(host, "b", "candidate", 80, machine: "rig-two");
            await Archive(host, "other", "candidate", 50, scenario: "different-workload");
            var result = await host.Json($"/api/v1/compare?A={A}&B={B}");
            Require(result.GetProperty("rows").EnumerateArray().All(row => row.GetProperty("changePercent").ValueKind == JsonValueKind.Null), "Mismatched contexts were combined.");
            Require(result.GetProperty("status").GetString() == "incomparable", "Context mismatch not surfaced.");
        }));
        checks.Add(("zero reference values have no invented percent change", async () =>
        {
            await using var host = new AgentFixture();
            await Archive(host, "zero", "baseline", 0);
            await Archive(host, "b", "candidate", 1);
            var result = await host.Json($"/api/v1/compare?A={A}&B={B}");
            Require(result.GetProperty("rows")[0].GetProperty("changePercent").ValueKind == JsonValueKind.Null, "Zero reference caused an invalid percentage.");
        }));
        checks.Add(("saved result indexing is idempotent and requires archived ownership", async () =>
        {
            await using var host = new AgentFixture();
            await Archive(host, "once", "baseline", 100);
            await host.Json("/api/v1/build-results/index", HttpMethod.Post, new { runId = "run-once" });
            Require((await host.Json("/api/v1/build-results/" + A)).GetProperty("runCount").GetInt32() == 1, "Repeated indexing duplicated a run.");
            Directory.Move(Path.Combine(host.Paths.Tested, "agent-once"), Path.Combine(host.Paths.Testing, "agent-once"));
            await host.Json("/api/v1/build-results/index", HttpMethod.Post, new { runId = "run-once" }, HttpStatusCode.Conflict);
        }));
        checks.Add(("malformed assessment and digest mismatch cannot become indexed successes", async () =>
        {
            await using var host = new AgentFixture();
            await Archive(host, "invalid", "baseline", 100, index: false);
            await File.WriteAllTextAsync(Path.Combine(host.Paths.Results, "run-invalid", "assessment.json"), "{}");
            await host.Json("/api/v1/build-results/index", HttpMethod.Post, new { runId = "run-invalid" }, HttpStatusCode.BadRequest);
            await host.Json("/api/v1/compare?A=bad&B=" + B, expected: HttpStatusCode.BadRequest);
        }));
        checks.Add(("pretty reports stay small and raw CSV remains a separate exact artifact", async () =>
        {
            await using var host = new AgentFixture();
            await Archive(host, "a", "baseline", 100);
            await Archive(host, "b", "candidate", 80);
            await host.Json("/api/v1/baseline", HttpMethod.Put, new { sha256 = A });
            using var pretty = await host.Client.GetAsync($"/api/v1/compare?A={A}&B={B}&format=markdown");
            var text = await pretty.Content.ReadAsStringAsync();
            Require(pretty.IsSuccessStatusCode && text.Contains("Metric") && text.Contains("improved"), "Pretty server report missing.");
            Require(Encoding.UTF8.GetByteCount(text) <= 4096, "Pretty comparison exceeds budget.");
            using var csv = await host.Client.GetAsync($"/api/v1/compare?A={A}&B={B}&format=csv");
            Require(csv.IsSuccessStatusCode && (await csv.Content.ReadAsStringAsync()).Contains("runtime"), "Full comparison CSV unavailable.");
            Require(await host.Client.GetStringAsync("/api/v1/runs/run-b/artifacts/metrics.csv") == "time,value\n0,80\n", "Raw CSV was replaced with summarized data.");
            var summary = await host.Json("/api/v1/build-results/" + B);
            Require(Encoding.UTF8.GetByteCount(summary.GetRawText()) <= 8192, "Build report is not bounded.");
            Require(summary.GetProperty("runs")[0].GetProperty("rawCsv").GetString()!.EndsWith("metrics.csv"), "Raw data link missing.");
        }));
    }

    private static async Task Archive(AgentFixture host, string id, string binary, double value,
        bool eligible = true, string executable = "candidate.exe", string machine = "rig", string scenario = "smoke", bool index = true)
    {
        host.State.SetPhase("fixture"); // Prevent background indexing until fixture publication is complete.
        var sha = Digest(binary);
        await host.Json("/api/v1/jobs", HttpMethod.Post, new
        {
            id, job = new { id, executable, arguments = new[] { scenario }, timeoutSeconds = 10, expectedExecutableSha256 = sha,
                requiredFiles = new[] { "workload.bin" }, inputs = new[] { new { path = "workload.bin", hash = true, expectedSha256 = Digest("workload") } } },
            files = new[] {
                new { path = executable, length = binary.Length, sha256 = sha, executable = true },
                new { path = "workload.bin", length = 8, sha256 = Digest("workload"), executable = false } }
        });
        var package = Path.Combine(host.Paths.Tested, "agent-" + id);
        Directory.Move(host.Draft(id), package);
        var runId = "run-" + id;
        var result = Path.Combine(host.Paths.Results, runId);
        Directory.CreateDirectory(result);
        AtomicJson.Write(Path.Combine(package, ".runner-attempt.json"), new { RunId = runId, Phase = "finalized", Attempt = 1 });
        AtomicJson.Write(Path.Combine(host.Paths.Pending, ".agent-jobs", id, "validation.json"), new { Passed = true, ExecutableSha256 = sha, Checks = Array.Empty<object>() });
        var assessment = new RunAssessment(ExecutionOutcome.Completed, eligible ? CorrectnessOutcome.Passed : CorrectnessOutcome.Failed,
            EvidenceOutcome.Complete, eligible ? ComparisonEligibility.Eligible : ComparisonEligibility.Ineligible,
            eligible ? [] : ["guest_failed"], []);
        host.Assessment(runId, assessment);
        AtomicJson.Write(Path.Combine(result, "result.json"), new
        {
            runId, job = id, executable, executableSha256 = sha, status = "completed", endedUtc = DateTimeOffset.UtcNow,
            assessment, workload = new { Measurements = new[] { new { Name = "runtime", Value = value, Unit = "ms", Direction = "lower" } } },
            host = new { runnerVersion = "fixture-v1", machine, os = "fixture-os", architecture = "X64" },
            monitoring = new { intervalMs = 100, gpuProviders = Array.Empty<string>() }
        });
        var jobHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(package, "job.json")))).ToLowerInvariant();
        AtomicJson.Write(Path.Combine(result, "input-manifest.json"), new { JobId = id, ExecutableSha256 = sha, JobManifestSha256 = jobHash });
        AtomicJson.Write(Path.Combine(result, "host-inventory.json"), new
        {
            CapturedUtc = DateTimeOffset.UtcNow, Machine = machine, OperatingSystem = "fixture-os", OsArchitecture = "X64",
            ProcessArchitecture = "X64", DotNet = "fixture", CpuModel = "fixture-cpu", LogicalProcessors = 4,
            PhysicalMemoryBytes = 8192L, PrimaryDisplay = "fixture", PowerScheme = "fixture", GraphicsAdapters = Array.Empty<object>()
        });
        await File.WriteAllTextAsync(Path.Combine(result, "metrics.csv"), "time,value\n0," + value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
        if (index) await host.Json("/api/v1/build-results/index", HttpMethod.Post, new { runId });
    }
}
