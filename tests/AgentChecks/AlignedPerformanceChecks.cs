using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Queue;
using XemuTestRunner.Runtime;
using static AgentFixture;

internal static class AlignedPerformanceChecks
{
    private static JobDefinition Job(string mode = "qemu-realtime-segment-v1",
        string? framePath = "guest-frames.log", string metricsPath = "metrics.csv",
        string? flipPath = "guest-flips.log", int flipTailSamples = 4) =>
        JsonSerializer.Deserialize<JobDefinition>(JsonSerializer.Serialize(new
        {
            Id = "aligned-analysis", Executable = "xemu", Workload = new
            {
                RequirePlanCompletion = false,
                Analysis = new { Segment = "game", MetricsPath = metricsPath, GuestFlipsPath = flipPath,
                    GuestFramesPath = framePath, FlipTailSamples = flipTailSamples,
                    FrameTailSeconds = 30, TimingMode = mode }
            }
        }), ConfigLoader.JsonOptions)!;

    private static async Task<string> Fixture(AgentFixture host, int lastSeconds = 110)
    {
        var root = Path.Combine(host.Paths.Results, "aligned");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "metrics.csv"),
            "segment,process_cpu_core_pct,collector_duty_pct,overrun\n" +
            "loading,999,99,1\ngame,10,0.1,0\ngame,20,0.2,0\ngame,30,0.3,0\n");
        await File.WriteAllTextAsync(Path.Combine(root, "segments.jsonl"),
            "{\"TimestampUtc\":\"2026-10-31T00:00:40Z\",\"ElapsedMs\":40000,\"Action\":\"start\",\"Name\":\"game\",\"ClockDomain\":\"linux-clock-monotonic\",\"HostTimestampUs\":40000000}\n" +
            "{\"TimestampUtc\":\"2026-10-31T00:01:40Z\",\"ElapsedMs\":100000,\"Action\":\"end\",\"Name\":\"game\",\"ClockDomain\":\"linux-clock-monotonic\",\"HostTimestampUs\":100000000}\n");
        await File.WriteAllTextAsync(Path.Combine(root, "guest-frames.log"),
            string.Concat(Enumerable.Range(0, (lastSeconds - 10) / 5 + 1).Select(i =>
                $"timestamp_us={(10L + i * 5) * 1000000} frame={i + 1} delta_us={(i == 0 ? 0 : 5000000)}\n")));
        await File.WriteAllTextAsync(Path.Combine(root, "guest-flips.log"),
            string.Concat(Enumerable.Range(0, (lastSeconds - 10) / 5).Select(i =>
                $"elapsed_us=5000000 frames={(i == 0 ? 2 : 1)} fps=0.2\n")));
        return root;
    }

    private static Task<WorkloadEvaluation> Evaluate(AgentFixture host, string root, JobDefinition? job = null) =>
        WorkloadEvaluator.EvaluateAsync(job ?? Job(), host.Root, root, null, 4, true, CancellationToken.None);

    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("saved analysis accepts the explicit QEMU segment clock contract", () =>
        {
            JobDefinition? job = null;
            try { job = Job(); }
            catch (JsonException) { }
            Require(job is not null,
                "The saved analysis cannot declare a QEMU source-clock alignment contract.");
            return Task.CompletedTask;
        }));
        checks.Add(("legacy descriptive analysis retains its pinned profile identity", async () =>
        {
            await using var host = new AgentFixture();
            await PerformanceAnalysisChecks.Analyze(host);
            var report = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(
                Path.Combine(host.Paths.Results, "analysis-run", "performance.json")));
            Require(report.GetProperty("ProfileSha256").GetString() ==
                "c6a3443006939316e73866d18daa92519c33cdb66ff45046eaa64faa934bd1e6",
                "Adding opt-in alignment changed a legacy saved-analysis identity.");
            Require(report.GetProperty("SchemaVersion").GetInt32() == 1,
                "A descriptive legacy report must preserve its original schema.");
        }));
        foreach (var invalid in new[]
        {
            (Mode: "unqualified-clock", Frames: (string?)"guest-frames.log", Metrics: "metrics.csv"),
            (Mode: "qemu-realtime-segment-v1", Frames: (string?)null, Metrics: "metrics.csv"),
            (Mode: "qemu-realtime-segment-v1", Frames: (string?)"guest-frames.log", Metrics: "segments.jsonl")
        })
        {
            checks.Add(($"aligned analysis rejects an unusable clock/source contract {invalid}", () =>
            {
                var rejected = false;
                try { _ = Job(invalid.Mode, invalid.Frames, invalid.Metrics); }
                catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
                { rejected = true; }
                Require(rejected, "An unusable aligned analysis contract was accepted.");
                return Task.CompletedTask;
            }));
        }
        checks.Add(("aligned analysis uses the fixed segment window and ignores later progress", async () =>
        {
            await using var host = new AgentFixture();
            var root = await Fixture(host, lastSeconds: 200);
            var result = await Evaluate(host, root);
            Require(result.Evidence == EvidenceOutcome.Complete,
                string.Join(";", result.Checks.Where(c => !c.Passed).Select(c => c.Detail)));
            var report = JsonSerializer.Deserialize<PerformanceReport>(
                await File.ReadAllTextAsync(Path.Combine(root, "performance.json")), ConfigLoader.JsonOptions)!;
            Require(report.SchemaVersion == 2 && report.Frames is { WindowStartUs: 70000000, WindowEndUs: 100000000 },
                "Later frame records moved the declared segment window.");
            Require(report.Frames!.IntervalsMs.Count == 6 && report.Frames.IntervalsMs.Mean == 5000,
                "The fixed window did not retain its six completed five-second intervals.");
            Require(result.Measurements.SingleOrDefault(m => m.Name == "guest/window_frame_events")?.Value == 6 &&
                result.Measurements.SingleOrDefault(m => m.Name == "guest/window_elapsed_s")?.Value == 30 &&
                result.Measurements.SingleOrDefault(m => m.Name == "guest/window_cadence_fps")?.Value == 0.2,
                "Guest event cadence lacks the fixed host-window exposure.");
            Require(report.Flips is { Samples: 4, Frames: 4, ElapsedUs: 20000000 } &&
                report.Flips.CadenceFps == 0.2, "Flip records were not restricted to the selected window.");
            Require(report.Sources.Any(s => s.Path == "segments.jsonl"), "Measurement anchors are not hash-pinned sources.");
            var view = await host.Json("/api/v1/runs/aligned/performance");
            Require(view.GetProperty("available").GetBoolean(), "The API rejected the saved aligned schema.");
            var markdown = await host.Client.GetStringAsync("/api/v1/runs/aligned/performance?format=markdown");
            Require(markdown.Contains("linux-clock-monotonic") && markdown.Contains("Fixed-window guest events"),
                "The readable report does not disclose aligned window scope.");
        }));
        foreach (var end in new[] { 35, 65, 85 })
        {
            checks.Add(($"aligned analysis rejects frame progress ending at {end}s before the 100s boundary", async () =>
            {
                await using var host = new AgentFixture();
                var root = await Fixture(host, end);
                var result = await Evaluate(host, root);
                Require(result.Evidence == EvidenceOutcome.Incomplete, "Stale progress became complete aligned evidence.");
                Require(!result.Measurements.Any(m => m.Name.StartsWith("guest/", StringComparison.Ordinal)),
                    "Stale frame/flip tails produced aligned guest performance values.");
            }));
        }
        foreach (var broken in new[] { "missing-clock", "unknown-clock", "mixed-clock", "shifted-origin", "duplicate-start", "abandoned", "overflow" })
        {
            checks.Add(($"aligned analysis rejects {broken} segment anchors", async () =>
            {
                await using var host = new AgentFixture();
                var root = await Fixture(host);
                var path = Path.Combine(root, "segments.jsonl");
                var lines = (await File.ReadAllLinesAsync(path)).ToList();
                switch (broken)
                {
                    case "missing-clock": lines = lines.Select(x => x.Replace(",\"ClockDomain\":\"linux-clock-monotonic\"", "")).ToList(); break;
                    case "unknown-clock": lines = lines.Select(x => x.Replace("linux-clock-monotonic", "unknown-clock")).ToList(); break;
                    case "mixed-clock": lines[1] = lines[1].Replace("linux-clock-monotonic", "windows-qpc"); break;
                    case "shifted-origin": lines[0] = lines[0].Replace("40000000}", "1040000000}"); lines[1] = lines[1].Replace("100000000}", "1100000000}"); break;
                    case "duplicate-start": lines.Insert(1, lines[0]); break;
                    case "abandoned": lines[1] = lines[1].Replace("\"end\"", "\"abandoned\""); break;
                    case "overflow": lines[1] = lines[1].Replace("100000000}", "9223372036854775807}"); break;
                }
                await File.WriteAllLinesAsync(path, lines);
                var result = await Evaluate(host, root);
                Require(result.Evidence == EvidenceOutcome.Incomplete, "Invalid segment anchors qualified aligned evidence.");
                Require(!result.Measurements.Any(m => m.Name.StartsWith("guest/", StringComparison.Ordinal)),
                    "Invalid segment anchors produced aligned guest performance values.");
            }));
        }
        foreach (var broken in new[] { "first-frame", "delta", "flip-count", "flip-time" })
        {
            checks.Add(($"aligned analysis rejects inconsistent producer origin {broken}", async () =>
            {
                await using var host = new AgentFixture();
                var root = await Fixture(host);
                var path = Path.Combine(root, broken.StartsWith("flip-", StringComparison.Ordinal)
                    ? "guest-flips.log" : "guest-frames.log");
                var lines = await File.ReadAllLinesAsync(path);
                lines[0] = broken switch
                {
                    "first-frame" => lines[0].Replace("frame=1", "frame=2"),
                    "delta" => lines[0].Replace("delta_us=0", "delta_us=1"),
                    "flip-count" => lines[0].Replace("frames=2", "frames=3"),
                    _ => lines[0].Replace("elapsed_us=5000000", "elapsed_us=5000001")
                };
                await File.WriteAllLinesAsync(path, lines);
                var result = await Evaluate(host, root);
                Require(result.Evidence == EvidenceOutcome.Incomplete, "Inconsistent producer logs became aligned evidence.");
                Require(!result.Measurements.Any(m => m.Name.StartsWith("guest/", StringComparison.Ordinal)),
                    "Inconsistent producer logs emitted aligned guest performance values.");
            }));
        }
        checks.Add(("aligned frame-only reports disclose a censored end gap without shifting exposure", async () =>
        {
            await using var host = new AgentFixture();
            var root = await Fixture(host);
            var times = Enumerable.Range(0, 21).Select(i => 10 + i * 5).Where(t => t != 100).ToArray();
            await File.WriteAllTextAsync(Path.Combine(root, "guest-frames.log"),
                string.Concat(times.Select((time, i) =>
                    $"timestamp_us={time * 1000000L} frame={i + 1} delta_us={(i == 0 ? 0 : (time - times[i - 1]) * 1000000L)}\n")));
            var result = await Evaluate(host, root, Job(flipPath: null));
            Require(result.Evidence == EvidenceOutcome.Complete, "A bracketed frame-only interval became unavailable.");
            Require(result.Measurements.SingleOrDefault(m => m.Name == "guest/window_frame_events")?.Value == 5 &&
                result.Measurements.SingleOrDefault(m => m.Name == "guest/window_elapsed_s")?.Value == 30 &&
                result.Measurements.SingleOrDefault(m => m.Name == "guest/window_end_gap_ms")?.Value == 5000,
                "The unfinished final interval was hidden or shifted out of the exposure.");
            var markdown = await host.Client.GetStringAsync("/api/v1/runs/aligned/performance?format=markdown");
            Require(markdown.Contains("5000.00") && markdown.Contains("not a completed frame latency"),
                "A censored gap was presented as a completed latency sample.");
        }));
        checks.Add(("aligned windows count zero-interval events separately from latency samples", async () =>
        {
            await using var host = new AgentFixture();
            var root = await Fixture(host);
            var times = Enumerable.Range(0, 21).Select(i => 10 + i * 5).ToList();
            times.Insert(times.IndexOf(80), 80);
            await File.WriteAllTextAsync(Path.Combine(root, "guest-frames.log"),
                string.Concat(times.Select((time, i) =>
                    $"timestamp_us={time * 1000000L} frame={i + 1} delta_us={(i == 0 ? 0 : (time - times[i - 1]) * 1000000L)}\n")));
            var result = await Evaluate(host, root, Job(flipPath: null));
            var report = JsonSerializer.Deserialize<PerformanceReport>(
                await File.ReadAllTextAsync(Path.Combine(root, "performance.json")), ConfigLoader.JsonOptions)!;
            Require(result.Evidence == EvidenceOutcome.Complete && report.Frames is { ZeroIntervals: 1 },
                "A zero-interval event invalidated a valid bounded source.");
            Require(result.Measurements.SingleOrDefault(m => m.Name == "guest/window_frame_events")?.Value == 7 &&
                report.Frames!.IntervalsMs.Count == 6, "Zero intervals were silently lost from event exposure or became latency samples.");
        }));
        checks.Add(("aligned analysis rejects absent segment evidence rather than guessing clock offsets", async () =>
        {
            await using var host = new AgentFixture();
            var root = await Fixture(host);
            File.Delete(Path.Combine(root, "segments.jsonl"));
            var result = await Evaluate(host, root);
            Require(result.Evidence == EvidenceOutcome.Incomplete &&
                !result.Measurements.Any(m => m.Name.StartsWith("guest/", StringComparison.Ordinal)),
                "An aligned report inferred an absent segment anchor.");
        }));
        checks.Add(("aligned analysis rejects a missing flip suffix despite fresh complete frames", async () =>
        {
            await using var host = new AgentFixture();
            var root = await Fixture(host);
            var path = Path.Combine(root, "guest-flips.log");
            var lines = await File.ReadAllLinesAsync(path);
            // The retained last group is70..75s; complete frame records continue through110s.
            await File.WriteAllLinesAsync(path, lines.Take(13));
            var result = await Evaluate(host, root, Job(flipTailSamples: 1));
            Require(result.Evidence == EvidenceOutcome.Incomplete,
                "A missing producer flip suffix became complete evidence.");
            Require(!result.Measurements.Any(m => m.Name.StartsWith("guest/", StringComparison.Ordinal)),
                "A truncated flip suffix emitted aligned guest measurements.");
        }));
        checks.Add(("aligned analysis rejects nominal flip windows without a phase margin", () =>
        {
            var rejected = false;
            try { _ = Job(flipTailSamples: 6); }
            catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
            { rejected = true; }
            Require(rejected, "Six producer groups of at least five seconds cannot fit a30s aligned window.");
            _ = Job(flipPath: null, flipTailSamples: 6);
            return Task.CompletedTask;
        }));
        checks.Add(("aligned analysis accepts realistic flush durations with nonaligned boundaries", async () =>
        {
            await using var host = new AgentFixture();
            var root = await Fixture(host);
            await File.WriteAllTextAsync(Path.Combine(root, "guest-frames.log"),
                string.Concat(Enumerable.Range(0, 21).Select(i =>
                    $"timestamp_us={10000000L + i * 5050000L} frame={i + 1} delta_us={(i == 0 ? 0 : 5050000)}\n")));
            await File.WriteAllTextAsync(Path.Combine(root, "guest-flips.log"),
                string.Concat(Enumerable.Range(0, 20).Select(i =>
                    $"elapsed_us=5050000 frames={(i == 0 ? 2 : 1)} fps=0.198\n")));
            var result = await Evaluate(host, root);
            var report = JsonSerializer.Deserialize<PerformanceReport>(
                await File.ReadAllTextAsync(Path.Combine(root, "performance.json")), ConfigLoader.JsonOptions)!;
            Require(result.Evidence == EvidenceOutcome.Complete && report.Frames?.IntervalsMs.Mean == 5050,
                "Healthy slightly-long flush groups lost aligned coverage.");
            Require(report.Flips is { Samples: 4, ElapsedUs: 20200000, WindowStartUs: 75650000, WindowEndUs: 95850000 } &&
                Math.Abs(report.Flips.CadenceFps - 0.19801980198019803) < 1e-12,
                "Nonaligned complete flip groups lost their actual bounds or weighted denominator.");
        }));
    }
}
