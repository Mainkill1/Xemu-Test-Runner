using System.Globalization;
using System.Text;
using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Reliability;
using XemuTestRunner.Runtime;

namespace XemuTestRunner.Networking;

public sealed partial class EmbeddedHttpServer
{
    private sealed record PerformanceCleanup(string State, int Files, int DeletedFiles, bool? HddDeleted);
    private sealed record PerformanceView(bool Available, string RunId, string? Code, PerformanceReport? Analysis,
        AgentOutcome? Outcome, string? ExecutableSha256, string? LaunchExecutableSha256, string? IdentityCode,
        PerformanceCleanup Cleanup, string Detail);

    private async Task<bool?> TryPerformanceRouteAsync(Stream stream, HttpRequest request, CancellationToken ct)
    {
        if (request.Method != "GET") return null;
        if (request.Path == "/api/v1/help" && GetQueryValue(request.Query, "topic") == "performance")
        {
            await WriteAgentJsonAsync(stream, new
            {
                capability = "performanceAnalysis", report = "/api/v1/runs/{runId}/performance",
                configuration = "Workload.Analysis pins segment, raw sources, tail windows and minimum sample coverage.",
                computation = "Once during post-exit workload finalization. GET reads the saved report only.",
                comparison = "/api/v1/compare?A={sha256}&B={sha256}",
                methods = new[] { "elapsed-weighted flip cadence", "linear percentiles at (n-1)*p", "per-attempt distributions" },
                rule = "Performance data does not override correctness, state qualification or canonical comparison eligibility. No SSH or agent analyzer is required."
            }, cancellationToken: ct).ConfigureAwait(false);
            return false;
        }
        const string prefix = "/api/v1/runs/";
        const string suffix = "/performance";
        if (!request.Path.StartsWith(prefix, StringComparison.Ordinal) || !request.Path.EndsWith(suffix, StringComparison.Ordinal) ||
            request.Path.Length <= prefix.Length + suffix.Length) return null;
        var id = Uri.UnescapeDataString(request.Path[prefix.Length..^suffix.Length]);
        if (!BuildResultStore.SafeRunId(id)) throw new InvalidDataException("Invalid performance run ID.");
        var format = GetQueryValue(request.Query, "format") ?? "json";
        if (format is not ("json" or "markdown")) throw new InvalidDataException("Performance format must be json or markdown.");
        var catalog = new EvidenceCatalog(_paths.Results);
        var reportPath = catalog.Resolve(id, "performance.json");
        if (!Directory.Exists(Path.GetDirectoryName(reportPath)))
            throw new AgentRequestException(404, "run_not_found", "No evidence directory has this run ID.", "Use an existing run ID, not a request ID.");
        var report = File.Exists(reportPath) ? ReadPerformanceDocument<PerformanceReport>(reportPath, 65536) : null;
        if (report is not null && (report.SchemaVersion != 1 || report.Sources is null || report.Errors is null))
            throw new InvalidDataException("Saved performance report is invalid.");
        var outcome = _assessmentReader.Read(_paths.Results, id).Outcome;
        var identity = new RecordedEmulatorIdentity(null, null, "executable_identity_missing");
        var manifestPath = catalog.Resolve(id, "input-manifest.json");
        if (File.Exists(manifestPath))
            identity = RecordedEmulatorIdentity.Read(ReadPerformanceDocument<JsonElement>(manifestPath, 1024 * 1024));
        var cleanup = new PerformanceCleanup("unavailable", 0, 0, null);
        var cleanupPath = catalog.Resolve(id, "runtime-cleanup.json");
        if (File.Exists(cleanupPath))
        {
            var receipt = ReadPerformanceDocument<JsonElement>(cleanupPath, 65536);
            var state = TryPerformanceProperty(receipt, "state", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "unknown";
            if (TryPerformanceProperty(receipt, "files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                var total = files.GetArrayLength();
                var deleted = files.EnumerateArray().Count(file => TryPerformanceProperty(file, "deleted", out var value) && value.ValueKind == JsonValueKind.True);
                cleanup = new(state, total, deleted, total == 0 ? null : state == "complete" && deleted == total);
            }
        }
        var view = new PerformanceView(report is not null, id, report is null ? "analysis_not_recorded" : null,
            report, outcome, identity.Sha256, identity.LaunchSha256, identity.Code, cleanup,
            $"/api/v1/runs/{Uri.EscapeDataString(id)}?view=summary");
        if (format == "json") await WriteAgentJsonAsync(stream, view, cancellationToken: ct).ConfigureAwait(false);
        else await WriteBuildTextAsync(stream, FormatPerformance(view), "markdown", ct).ConfigureAwait(false);
        return false;
    }

    private static T ReadPerformanceDocument<T>(string path, long limit)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length > limit) throw new InvalidDataException("Performance metadata exceeds its read bound.");
        return JsonSerializer.Deserialize<T>(file, ConfigLoader.JsonOptions) ?? throw new InvalidDataException("Performance metadata is empty.");
    }
    private static bool TryPerformanceProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default; return false;
    }
    private static string FormatPerformance(PerformanceView value)
    {
        static string N(double n) => n.ToString("0.00", CultureInfo.InvariantCulture);
        var text = new StringBuilder();
        text.AppendLine($"Run {value.RunId} | execution={value.Outcome?.Execution ?? "unavailable"} | correctness={value.Outcome?.Correctness ?? "unavailable"} | evidence={value.Outcome?.Evidence ?? "unavailable"} | comparison={value.Outcome?.Comparison ?? "unavailable"}");
        text.AppendLine($"Executable: {value.ExecutableSha256 ?? "unavailable"}; cleanup={value.Cleanup.State}; deleted={value.Cleanup.DeletedFiles}/{value.Cleanup.Files}");
        if (value.LaunchExecutableSha256 != value.ExecutableSha256) text.AppendLine("Launcher: " + value.LaunchExecutableSha256);
        if (value.IdentityCode is not null) text.AppendLine("Identity: " + value.IdentityCode);
        if (value.Analysis is not { } report)
        {
            text.AppendLine("No saved analysis. Add Workload.Analysis to a new test revision before running; this GET does not scan raw files."); return text.ToString();
        }
        text.AppendLine($"Profile {report.ProfileSha256[..12]} | segment={report.Profile.Segment} | complete={report.Complete}");
        text.AppendLine("| Metric | Value | Samples / exposure |"); text.AppendLine("|---|---:|---|");
        if (report.Monitoring is { } monitor)
        {
            text.AppendLine($"| CPU mean / median | {N(monitor.Cpu.Mean)} / {N(monitor.Cpu.Median)} core % | {monitor.Cpu.Count} samples; {monitor.MissingCpuSamples} missing |");
            text.AppendLine($"| Collector duty mean | {N(monitor.CollectorDuty.Mean)} % | {monitor.CollectorDuty.Count} samples |");
            text.AppendLine($"| Collector overruns | {monitor.Overruns} | {monitor.Rows} segment rows |");
        }
        if (report.Flips is { } flips)
            text.AppendLine($"| Guest cadence | {N(flips.CadenceFps)} fps | {flips.Frames} frames / {N(flips.ElapsedUs / 1000000.0)} s; last {flips.Samples} records |");
        if (report.Frames is { } frames)
        {
            var d = frames.IntervalsMs;
            text.AppendLine($"| Frame mean / p50 / p95 / p99 | {N(d.Mean)} / {N(d.Median)} / {N(d.P95)} / {N(d.P99)} ms | {d.Count} samples; last {report.Profile.FrameTailSeconds} s |");
            text.AppendLine($"Frame source timestamps: {frames.FirstSampleUs}..{frames.LastSampleUs} us; zero intervals excluded: {frames.ZeroIntervals}.");
        }
        foreach (var error in report.Errors) text.AppendLine("Unavailable: " + error.Replace('\r', ' ').Replace('\n', ' '));
        text.AppendLine("Descriptive recorded metrics, not proof of correctness or statistical significance. Raw files remain separate evidence.");
        return text.ToString();
    }
}
