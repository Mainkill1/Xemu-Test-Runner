using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;
using XemuTestRunner.Runtime;
using static AgentFixture;

internal static class GuestPipelineChecks
{
    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("full guest suite flows from HDD through canonical indexing to A/B CSV", async () =>
        {
            await using var host = new AgentFixture();
            host.State.SetPhase("fixture");
            var reference = Suite(100);
            await Archive(host, "a", "baseline", reference, reference);
            await Archive(host, "b", "candidate", Suite(80), reference);
            var shaA = Digest("baseline"); var shaB = Digest("candidate");
            await host.Json("/api/v1/baseline", HttpMethod.Put, new { sha256 = shaA });
            var comparison = await host.Json("/api/v1/compare?B=" + shaB);
            Require(comparison.GetProperty("baselinePinned").GetBoolean(), "Guest report ignored the saved baseline.");
            Require(comparison.GetProperty("rows")[0].GetProperty("improvementPercent").GetDouble() == 20, "Guest timing improvement was not computed on the tester.");
            Require(comparison.GetProperty("moreRows").GetInt32() > 0, "Full guest suite was silently hidden by the summary cap.");
            var csv = await host.Client.GetStringAsync("/api/v1/compare?B=" + shaB + "&format=csv");
            var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Require(lines.Length == 144 * 5 + 1, "Complete per-leaf timing statistics did not survive indexing.");
            Require(lines.Skip(1).All(line => line.TrimEnd().EndsWith(",20", StringComparison.Ordinal)), "Guest statistics have missing or incorrect improvement percentages.");
            Require(await host.Client.GetStringAsync("/api/v1/runs/run-b/artifacts/guest/results.txt") == Suite(80), "Extracted raw guest evidence is not API-downloadable byte-for-byte.");
            var run = await host.Json("/api/v1/runs/run-b?view=summary");
            Require(run.GetProperty("outcome").GetProperty("correctness").GetString() == "passed", "Guest oracle assessment did not reach the run summary.");
        }));
        checks.Add(("adding optional extraction does not rewrite old baked workload serialization", () =>
        {
            var workload = JsonSerializer.Serialize(new WorkloadContract(), XemuTestRunner.Config.ConfigLoader.JsonOptions);
            Require(!workload.Contains("GuestHddResults", StringComparison.Ordinal), "Null extraction settings changed old content-addressed test definitions.");
            return Task.CompletedTask;
        }));
    }

    private static string Suite(int microseconds)
    {
        var records = new List<object>();
        for (var i = 0; i < 144; i++) records.Add(new
        {
            schema_version = 1, id = (i < 72 ? "cpu" : "gpu") + ".test_" + i, revision = 1, kind = "leaf", name = "Fixture::" + i,
            outcome = "PASS", iterations = 3, sample_count = 3, measurement_iterations_multiplier = 1,
            warmup_iterations = 0, gpu_completion_mode = "per_iteration", unit = "us", direction = "lower_is_better",
            raw_results = new[] { microseconds, microseconds, microseconds }, work_checksum = "abcd"
        });
        for (var i = 0; i < 5; i++) records.Add(new
        {
            schema_version = 1, id = "group.summary_" + i, revision = 1, kind = "group", name = "Group::" + i,
            outcome = "PASS", iterations = 0, sample_count = 0, measurement_iterations_multiplier = 1,
            warmup_iterations = 0, gpu_completion_mode = "per_iteration", child_result_count = i + 1,
            raw_results = Array.Empty<int>()
        });
        return JsonSerializer.Serialize(records);
    }

    private static async Task Archive(AgentFixture host, string id, string binary, string payload, string reference)
    {
        var seedTemporary = Path.Combine(host.Root, "seed-" + id);
        GuestDiskFixture.Create(seedTemporary, null);
        var seedBytes = await File.ReadAllBytesAsync(seedTemporary);
        var sha = Digest(binary);
        await host.Json("/api/v1/jobs", HttpMethod.Post, new
        {
            id, job = new {
                id, executable = "xemu.bin", expectedExecutableSha256 = sha, timeoutSeconds = 10,
                requiredFiles = new[] { "reference.json" },
                workload = new { guestHddResults = new {
                    image = "disk.img", partitionOffsetBytes = 0, partitionLengthBytes = GuestDiskFixture.Size,
                    guestPath = "xemu_perf_tests/results.txt", expectedResults = "reference.json", expectedResultsSha256 = Digest(reference)
                }}
            },
            files = new[] {
                new { path = "xemu.bin", length = (long)binary.Length, sha256 = sha, executable = true },
                new { path = "seed.img", length = (long)seedBytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(seedBytes)).ToLowerInvariant(), executable = false },
                new { path = "reference.json", length = (long)Encoding.UTF8.GetByteCount(reference), sha256 = Digest(reference), executable = false }
            }
        });
        var package = host.Draft(id);
        File.Move(seedTemporary, Path.Combine(package, "seed.img"));
        await File.WriteAllTextAsync(Path.Combine(package, "reference.json"), reference);
        await File.WriteAllTextAsync(Path.Combine(package, "xemu.bin"), binary);
        var runtime = Path.Combine(host.Root, "runtime-" + id);
        Directory.CreateDirectory(runtime);
        GuestDiskFixture.Create(Path.Combine(runtime, "disk.img"), payload, qcow: true);
        var runId = "run-" + id;
        var result = Path.Combine(host.Paths.Results, runId);
        Directory.CreateDirectory(result);
        AtomicJson.Write(Path.Combine(package, AttemptJournal.FileName), new { RunId = runId, Attempt = 1, Phase = "exited" });
        var job = JobDefinition.LoadPackage(package);
        var evaluation = await WorkloadEvaluator.EvaluateAsync(job, package, result,
            new RuntimeMaterialization(runtime, [new RuntimeFileMaterialization("seed.img", "disk.img", seedBytes.Length, "fixture")]), 0, true, CancellationToken.None);
        Require(evaluation.Correctness == CorrectnessOutcome.Passed && evaluation.Evidence == EvidenceOutcome.Complete,
            "Guest suite evaluation failed: " + string.Join("; ", evaluation.Checks.Where(check => !check.Passed).Select(check => check.Detail)));
        Require(evaluation.Measurements.Count == 720, "Wrong number of extracted leaf metrics.");
        var assessment = new RunAssessment(ExecutionOutcome.Completed, evaluation.Correctness, evaluation.Evidence,
            ComparisonEligibility.Eligible, [], evaluation.Checks);
        host.Assessment(runId, assessment);
        AtomicJson.Write(Path.Combine(result, "result.json"), new {
            runId, job = id, executableSha256 = sha, status = "completed", workload = evaluation, assessment,
            host = new { runnerVersion = "fixture-v1" }, monitoring = new { intervalMs = 100, gpuProviders = Array.Empty<string>() }
        });
        AtomicJson.Write(Path.Combine(result, "input-manifest.json"), new {
            JobId = id, ExecutableSha256 = sha,
            JobManifestSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(package, "job.json")))).ToLowerInvariant()
        });
        AtomicJson.Write(Path.Combine(result, "host-inventory.json"), new {
            Machine = "fixture", OperatingSystem = "fixture", OsArchitecture = "X64", ProcessArchitecture = "X64",
            DotNet = "fixture", CpuModel = "fixture", LogicalProcessors = 4, GraphicsAdapters = Array.Empty<object>()
        });
        AtomicJson.Write(Path.Combine(host.Paths.Pending, ".agent-jobs", id, "validation.json"), new {
            Passed = true, ExecutableSha256 = sha, Checks = Array.Empty<object>()
        });
        AtomicJson.Write(Path.Combine(package, AttemptJournal.FileName), new { RunId = runId, Attempt = 1, Phase = "finalized" });
        Directory.Move(package, Path.Combine(host.Paths.Tested, "agent-" + id));
        await host.Json("/api/v1/build-results/index", HttpMethod.Post, new { runId });
    }
}
