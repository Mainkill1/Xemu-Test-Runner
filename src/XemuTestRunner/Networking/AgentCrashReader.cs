using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Diagnostics;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Networking;

internal sealed record AgentCrashBrief(bool Crashed, string Platform, string? Code, string Source,
    string Capture, string Dump, string Analysis, IReadOnlyList<string> TopFrames, IReadOnlyList<string> Issues);
internal sealed record AgentBundleBrief(string State, long Bytes, string? Sha256, string? Href,
    string MirrorState, int OmittedFiles, IReadOnlyList<string> Issues);
internal sealed record AgentCrashView(bool Ok, string RunId, bool Available, AgentCrashBrief? Crash,
    AgentBundleBrief? Bundle, string Report, string BundleReceipt);

internal static class AgentCrashReader
{
    public static AgentCrashView Read(string results, string runId)
    {
        var catalog = new EvidenceCatalog(results);
        if (!Directory.Exists(catalog.Resolve(runId, ".")))
            throw new AgentRequestException(404, "run_not_found", "No run has this ID.", "Follow the completed job's run link.");
        var prefix = "/api/v1/runs/" + Uri.EscapeDataString(runId) + "/artifacts/";
        var crash = Load<CrashReport>(catalog.Resolve(runId, "crash/report.json"));
        var bundle = Load<DiagnosticBundle>(catalog.Resolve(runId, "diagnostic-bundle.json"));
        if (crash is not null && (crash.RunId != runId || crash.TopFrames is null || crash.Issues is null))
            throw new InvalidDataException("Crash report identity/fields are invalid.");
        if (bundle is not null && (bundle.Bytes < 0 || bundle.Issues is null ||
            bundle.Artifact is not (null or "diagnostics.zip") ||
            (bundle.Artifact is not null && (bundle.Sha256 is not { Length: 64 } digest || !digest.All(Uri.IsHexDigit)))))
            throw new InvalidDataException("Diagnostic bundle receipt is invalid.");
        var brief = crash is null ? null : new AgentCrashBrief(crash.Crashed, crash.Platform, crash.Code,
            crash.Source, crash.Capture, crash.Dump, crash.Analysis,
            crash.TopFrames.Take(3).Select(Clip).ToArray(), crash.Issues.Take(3).Select(Clip).ToArray());
        var archive = bundle is null ? null : new AgentBundleBrief(bundle.State, bundle.Bytes, bundle.Sha256,
            bundle.Artifact is null ? null : prefix + "diagnostics.zip", bundle.MirrorState, bundle.OmittedFiles,
            bundle.Issues.Take(3).Select(Clip).ToArray());
        var view = new AgentCrashView(true, runId, brief is not null, brief, archive, prefix + "crash/report.json", prefix + "diagnostic-bundle.json");
        while (JsonSerializer.SerializeToUtf8Bytes(view, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length > 4096)
        {
            if (view.Crash is { TopFrames.Count: > 0 } frames)
                view = view with { Crash = frames with { TopFrames = frames.TopFrames.SkipLast(1).ToArray() } };
            else if (view.Crash is { Issues.Count: > 0 } issues)
                view = view with { Crash = issues with { Issues = issues.Issues.SkipLast(1).ToArray() } };
            else break;
        }
        return view;
    }

    private static string Clip(string value) => value.Length > 96 ? value[..93] + "..." : value;
    private static T? Load<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > 65536) throw new InvalidDataException("Diagnostic metadata exceeds 64 KiB.");
        return JsonSerializer.Deserialize<T>(stream, ConfigLoader.JsonOptions)
            ?? throw new InvalidDataException("Diagnostic metadata is empty.");
    }
}
