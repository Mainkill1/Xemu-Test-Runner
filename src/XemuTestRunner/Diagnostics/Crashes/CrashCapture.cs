using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Diagnostics;

public sealed partial class CrashCapture
{
    private readonly DiagnosticsOptions _diagnostics;
    private readonly CrashCaptureOptions _options;
    private readonly string _runId, _package, _executable, _directory;
    private readonly string? _sha;
    private readonly DateTimeOffset _started;
    private readonly Dictionary<string, (long Bytes, DateTime Written)> _initialReports = new(StringComparer.Ordinal);
    private readonly List<string> _issues = [];
    private readonly List<string> _frames = [];
    private string _dump = "unavailable", _analysis = "unavailable";
    private bool _osReport;

    public CrashCapture(DiagnosticsOptions diagnostics, string runId, string package, string executable,
        string? sha256, string resultDirectory, DateTimeOffset startedUtc)
    {
        _diagnostics = diagnostics; _options = diagnostics.CrashReports; _runId = runId; _package = package;
        _executable = executable; _sha = sha256;
        _directory = Path.Combine(resultDirectory, "crash"); _started = startedUtc;
        foreach (var name in _options.ReportFiles)
        {
            try
            {
                var file = new FileInfo(JobDefinition.ResolveInsidePackage(package, name));
                if (file.Exists) _initialReports[name] = (file.Length, file.LastWriteTimeUtc);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            { _issues.Add("report_baseline_unavailable:" + name); }
        }
    }

    public async Task<CrashReport> CollectAsync(int? pid, int? exitCode, NativeExitStatus? native,
        bool runnerTerminated, bool exited, string executionStatus)
    {
        var classification = CrashClassification.Classify(OperatingSystem.IsWindows(), exitCode, native, runnerTerminated);
        var report = new CrashReport(_runId, _sha, OperatingSystem.IsWindows() ? "windows" : "linux", pid,
            exitCode, runnerTerminated, classification.Crashed, classification.Source, classification.Code,
            "notNeeded", "notRequested", "notRequested", [], []);
        if (!_options.Enabled) return report with { Capture = "disabled" };
        if (!exited) return report with { Capture = "targetNotTerminated", Issues = ["Postmortem collection does not attach to or preserve a live target."] };
        using var deadline = new CancellationTokenSource(_options.TimeoutMs);
        var ct = deadline.Token;
        try
        {
            Directory.CreateDirectory(_directory);
            if (!OperatingSystem.IsWindows() && native is null)
                _issues.Add("native_wait_status_unavailable: exit codes alone cannot confirm a fatal signal");
            if (executionStatus != "completed" && pid is int processId)
            {
                if (OperatingSystem.IsWindows())
                    report = await CollectWindowsAsync(report, processId, ct).ConfigureAwait(false);
                else if (OperatingSystem.IsLinux())
                    report = await CollectLinuxAsync(report, processId, ct).ConfigureAwait(false);
                await CollectApplicationReportsAsync(ct).ConfigureAwait(false);
                report = report with { Capture = _osReport ? "captured" : "partial", Dump = _dump, Analysis = _analysis };
            }
            report = report with { TopFrames = _frames.Take(8).ToArray(), Issues = _issues.Take(12).ToArray() };
        }
        catch (OperationCanceledException)
        { report = report with { Capture = "timedOut", Dump = _dump, Analysis = _analysis, Issues = [.. _issues.Take(10), "capture_deadline"] }; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException or System.Xml.XmlException or System.Security.SecurityException)
        { report = report with { Capture = "failed", Dump = _dump, Analysis = _analysis, Issues = [.. _issues.Take(10), Clip(error.Message)] }; }
        // A reporting failure must not replace the primary execution result.
        try
        {
            AtomicJson.Write(Path.Combine(_directory, "report.json"), report);
            await File.WriteAllTextAsync(Path.Combine(_directory, "report.txt"),
                $"{(report.Crashed ? "CRASHED" : executionStatus.ToUpperInvariant())} | {report.Platform} | {report.Code ?? "no fatal exception confirmed"}\n" +
                $"Executable SHA-256: {_sha}\nCapture: {report.Capture}; dump: {report.Dump}; analysis: {report.Analysis}\n" +
                string.Join('\n', report.TopFrames) + "\n" + string.Join('\n', report.Issues), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { report = report with { Capture = "failed", Issues = [.. report.Issues.Take(10), "report_write_failed:" + Clip(error.Message)] }; }
        return report;
    }

    private async Task CollectApplicationReportsAsync(CancellationToken ct)
    {
        foreach (var name in _options.ReportFiles)
        {
            ct.ThrowIfCancellationRequested();
            var path = JobDefinition.ResolveInsidePackage(_package, name);
            if (!File.Exists(path)) { _issues.Add("report_missing:" + name); continue; }
            RejectLinks(path, _package);
            var file = new FileInfo(path);
            if (_initialReports.TryGetValue(name, out var old) && old == (file.Length, file.LastWriteTimeUtc))
            { _issues.Add("stale_report_excluded:" + name); continue; }
            if (file.LastWriteTimeUtc < _started.UtcDateTime.AddSeconds(-1))
            { _issues.Add("stale_report_excluded:" + name); continue; }
            var target = Path.Combine(_directory, "application", name);
            if (await CopyBoundedAsync(path, target, ct).ConfigureAwait(false)) _osReport = true;
        }
    }

    private async Task<bool> CopyBoundedAsync(string source, string target, CancellationToken ct)
    {
        if (new FileInfo(source).Length > _options.MaxArtifactBytes) { _issues.Add("artifact_size_limit:" + Path.GetFileName(source)); return false; }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + ".part";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                var length = input.Length;
                if (length > _options.MaxArtifactBytes) { _issues.Add("artifact_size_limit"); return false; }
                var buffer = new byte[65536]; long written = 0;
                while (written < length)
                {
                    var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - written)), ct).ConfigureAwait(false);
                    if (count == 0) throw new IOException("Diagnostic file changed during collection.");
                    await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false); written += count;
                }
                if (input.Length != length) throw new IOException("Diagnostic file grew during collection.");
                await output.FlushAsync(ct).ConfigureAwait(false);
            }
            File.Move(partial, target, false);
            return true;
        }
        finally { try { File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private async Task SaveToolAsync(string name, CrashToolResult result, CancellationToken ct)
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, name + ".txt"), result.Output, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(_directory, name + ".stderr.txt"), result.Error, ct).ConfigureAwait(false);
        if (result.State != "captured") _issues.Add(name + ":" + result.State + ":" + Clip(result.Error));
    }
    private void ExtractFrames(string output) => _frames.AddRange(output.Split('\n').Where(line => line.TrimStart().StartsWith('#')).Take(8).Select(Clip));
    internal static string Clip(string value) => value.Length > 240 ? value[..237] + "..." : value;
    internal static void RejectLinks(string path, string root)
    {
        var current = Path.GetFullPath(path); var stop = Path.GetFullPath(root);
        while (true)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked diagnostic paths are excluded.");
            if (current == stop) break;
            current = Path.GetDirectoryName(current) ?? throw new InvalidDataException("Diagnostic path escaped its root.");
        }
    }
}
