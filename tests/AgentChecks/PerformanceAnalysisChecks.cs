using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Queue;
using XemuTestRunner.Runtime;
using static AgentFixture;

internal static class PerformanceAnalysisChecks
{
    private static JobDefinition Job(string segment = "in-game-throttle", string frames = "guest-frames.log") =>
        JsonSerializer.Deserialize<JobDefinition>(JsonSerializer.Serialize(new
        {
            Id = "analysis", Executable = "xemu", Workload = new
            {
                RequirePlanCompletion = false,
                Analysis = new { Segment = segment, GuestFlipsPath = "guest-flips.log", GuestFramesPath = frames,
                    FlipTailSamples = 6, FrameTailSeconds = 30 }
            }
        }), ConfigLoader.JsonOptions)!;

    internal static async Task<WorkloadEvaluation> Analyze(AgentFixture host, string run = "analysis-run")
    {
        var root = Path.Combine(host.Paths.Results, run);
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "metrics.csv"),
            "segment,process_cpu_core_pct,collector_duty_pct,overrun\r\nloading,999,99,1\r\nin-game-throttle,10,0.1,0\r\nin-game-throttle,20,0.2,1\r\nin-game-throttle,30,0.3,0\r\n");
        // The first interval is deliberately very different and outside the last six.
        await File.WriteAllTextAsync(Path.Combine(root, "guest-flips.log"),
            "elapsed_us=1000000 frames=999 fps=999.0\n" +
            string.Concat(Enumerable.Range(1, 6).Select(i => $"elapsed_us={i * 1000000} frames=60 fps=60.0\n")));
        await File.WriteAllTextAsync(Path.Combine(root, "guest-frames.log"),
            string.Concat(Enumerable.Range(0, 6).Select(i => $"timestamp_us={i * 10000000L} frame={i} delta_us={(i + 1) * 1000}\n")));
        return await WorkloadEvaluator.EvaluateAsync(Job(), host.Root, root, null, 4, true, CancellationToken.None);
    }

    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("saved analysis computes segment statistics weighted cadence and interpolated frame tails", async () =>
        {
            await using var host = new AgentFixture();
            var result = await Analyze(host);
            double Metric(string name) => result.Measurements.Single(m => m.Name == name).Value;
            Require(Metric("monitor/cpu_mean") == 20 && Metric("monitor/cpu_median") == 20, "Segment filtering or CPU statistics failed.");
            Require(Metric("monitor/cpu_samples") == 3 && Metric("monitor/overruns") == 1, "Coverage or overruns were not retained.");
            Require(Math.Abs(Metric("guest/cadence_fps") - 360.0 / 21) < 1e-10, "Cadence averaged printed FPS rather than weighting elapsed time.");
            Require(Metric("guest/flip_frames") == 360 && Metric("guest/flip_elapsed_s") == 21, "Flip denominator is not inspectable.");
            Require(Metric("guest/interval_samples") == 4 && Metric("guest/interval_mean_ms") == 4.5, "Last-30-second window changed.");
            Require(Metric("guest/interval_p50_ms") == 4.5 && Math.Abs(Metric("guest/interval_p95_ms") - 5.85) < 1e-10 &&
                Math.Abs(Metric("guest/interval_p99_ms") - 5.97) < 1e-10, "Percentile interpolation differs from the declared algorithm.");
            Require(result.Evidence == EvidenceOutcome.Complete, string.Join(";", result.Checks.Select(c => c.Detail)));
            Require(File.Exists(Path.Combine(host.Paths.Results, "analysis-run", "performance.json")), "No precomputed report was saved.");
        }));
        checks.Add(("performance API reads precomputed summaries and preserves cleanup and outcomes", async () =>
        {
            await using var host = new AgentFixture();
            await Analyze(host);
            host.Assessment("analysis-run", new RunAssessment(ExecutionOutcome.Completed, CorrectnessOutcome.Passed, EvidenceOutcome.Complete, ComparisonEligibility.Ineligible, ["state_unmanaged"], []));
            var root = Path.Combine(host.Paths.Results, "analysis-run");
            await File.WriteAllTextAsync(Path.Combine(root, "input-manifest.json"), "{\"ExecutableSha256\":\"" + new string('a',64) + "\",\"Inputs\":[]}");
            await File.WriteAllTextAsync(Path.Combine(root, "runtime-cleanup.json"), "{\"state\":\"complete\",\"files\":[{\"deleted\":true}]}");
            var first = await host.Json("/api/v1/runs/analysis-run/performance");
            Require(first.GetProperty("available").GetBoolean(), "Performance summary is missing.");
            Require(first.GetProperty("outcome").GetProperty("comparison").GetString() == "ineligible", "Performance data overrode canonical eligibility.");
            Require(first.GetProperty("cleanup").GetProperty("hddDeleted").GetBoolean(), "Cleanup evidence is not available in the report.");
            foreach (var name in new[] { "metrics.csv", "guest-flips.log", "guest-frames.log" }) File.Delete(Path.Combine(root, name));
            var second = await host.Json("/api/v1/runs/analysis-run/performance");
            Require(second.GetProperty("analysis").GetRawText() == first.GetProperty("analysis").GetRawText(), "GET re-analyzed raw files instead of returning the saved report.");
            Require(second.GetRawText().Length < 8192, "Routine performance report is too large.");
            var markdown = await host.Client.GetStringAsync("/api/v1/runs/analysis-run/performance?format=markdown");
            Require(markdown.Contains("20.00") && markdown.Contains("ineligible"), "Readable report lost formatting or eligibility.");
        }));
        checks.Add(("missing data and nonfinite samples never become passing performance evidence", async () =>
        {
            await using var host = new AgentFixture();
            await Analyze(host);
            var root = Path.Combine(host.Paths.Results, "analysis-run");
            await File.WriteAllTextAsync(Path.Combine(root, "metrics.csv"), "segment,process_cpu_core_pct,collector_duty_pct,overrun\nin-game-throttle,NaN,Infinity,0\n");
            var result = await WorkloadEvaluator.EvaluateAsync(Job(), host.Root, root, null, 1, true, CancellationToken.None);
            Require(result.Evidence == EvidenceOutcome.Incomplete && result.Checks.Any(c => !c.Passed), "Nonfinite samples were accepted.");
            Require(!result.Measurements.Any(m => m.Name == "monitor/cpu_mean"), "Invalid CPU sample produced a measurement.");
            File.Delete(Path.Combine(root, "guest-frames.log"));
            result = await WorkloadEvaluator.EvaluateAsync(Job(), host.Root, root, null, 1, true, CancellationToken.None);
            Require(result.Evidence == EvidenceOutcome.Incomplete, "Missing frame source became a pass.");
        }));
        checks.Add(("frame timestamp regressions and zero flip durations are explicit failures", async () =>
        {
            await using var host = new AgentFixture();
            await Analyze(host);
            var root = Path.Combine(host.Paths.Results, "analysis-run");
            await File.WriteAllTextAsync(Path.Combine(root, "guest-frames.log"), "timestamp_us=200 frame=2 delta_us=10\ntimestamp_us=100 frame=3 delta_us=10\n");
            await File.WriteAllTextAsync(Path.Combine(root, "guest-flips.log"), "elapsed_us=0 frames=60 fps=60.0\n");
            var result = await WorkloadEvaluator.EvaluateAsync(Job(), host.Root, root, null, 4, true, CancellationToken.None);
            Require(result.Evidence == EvidenceOutcome.Incomplete, "Bad timing contracts were accepted.");
            Require(!result.Measurements.Any(m => m.Name == "guest/cadence_fps" || m.Name == "guest/interval_p99_ms"), "Malformed timing generated a speed metric.");
        }));
        checks.Add(("analysis preserves source bytes and pins their digests", async () =>
        {
            await using var host = new AgentFixture();
            await Analyze(host);
            var root = Path.Combine(host.Paths.Results, "analysis-run");
            var bytes = await File.ReadAllBytesAsync(Path.Combine(root, "metrics.csv"));
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            await WorkloadEvaluator.EvaluateAsync(Job(), host.Root, root, null, 4, true, CancellationToken.None);
            Require((await File.ReadAllBytesAsync(Path.Combine(root, "metrics.csv"))).SequenceEqual(bytes), "Analysis rewrote its raw input.");
            Require((await File.ReadAllTextAsync(Path.Combine(root, "performance.json"))).Contains(sha), "Source digest is missing from the analysis receipt.");
        }));
        checks.Add(("analysis rejects escaped paths and absence remains explicit", async () =>
        {
            await using var host = new AgentFixture();
            var rejected = false;
            try { _ = Job(frames: "../outside.log"); }
            catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException) { rejected = true; }
            Require(rejected, "Analysis profile accepted a path outside run evidence.");
            Directory.CreateDirectory(Path.Combine(host.Paths.Results, "no-analysis"));
            var missing = await host.Json("/api/v1/runs/no-analysis/performance");
            Require(!missing.GetProperty("available").GetBoolean(), "Legacy evidence was silently analyzed by GET.");
        }));
    }
}
