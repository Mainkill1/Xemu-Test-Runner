using System.Globalization;
using System.Text;
using XemuTestRunner.Diagnostics;

namespace XemuTestRunner.Networking;

public sealed partial class EmbeddedHttpServer
{
    public DiagnosticsOptions CrashDiagnostics { get; set; } = new();
    private async Task<bool?> TryCrashRoutesAsync(Stream stream, HttpRequest request, CancellationToken ct)
    {
        if (request.Method != "GET") return null;
        if (request.Path == "/api/v1/diagnostics/crash-capabilities")
        {
            var options = CrashDiagnostics;
            var linux = OperatingSystem.IsLinux();
            bool Has(string name) => ToolProcess.ResolveExecutable(name) is not null;
            await WriteAgentJsonAsync(stream, new
            {
                platform = linux ? "linux" : "windows", enabled = options.CrashReports.Enabled,
                nativeSignalStatus = linux && options.CrashReports.PreserveNativeExitStatus &&
                    Has(options.PythonExecutable) && File.Exists(Path.Combine(AppContext.BaseDirectory, "tools", "runner_posix.py")),
                reportProvider = linux ? "systemd-coredump" : "Windows Application Error / WER LocalDumps",
                tools = linux ? new[] { new { name = "journalctl", available = Has(options.CrashReports.JournalctlExecutable) },
                    new { name = "coredumpctl", available = Has(options.CrashReports.CoredumpctlExecutable) },
                    new { name = "gdb", available = Has(options.GdbExecutable) } } :
                    new[] { new { name = "cdb", available = Has(options.CrashReports.CdbExecutable) } },
                captureTimeoutMs = options.CrashReports.TimeoutMs, bundleTimeoutMs = options.CrashReports.BundleTimeoutMs,
                maxArtifactBytes = options.CrashReports.MaxArtifactBytes, maxBundleBytes = options.CrashReports.MaxBundleBytes,
                readiness = "Tool presence does not certify OS dump configuration, permissions or matching symbols. Those are checked per captured attempt; the runner does not change global OS settings.",
                results = "/api/v1/runs/{runId}/diagnostics"
            }, cancellationToken: ct).ConfigureAwait(false);
            return false;
        }
        const string prefix = "/api/v1/runs/";
        if (!request.Path.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var parts = request.Path[prefix.Length..].Split('/');
        if (parts.Length != 2 || parts[1] is not ("diagnostics" or "crash")) return null;
        var value = AgentCrashReader.Read(_paths.Results, Uri.UnescapeDataString(parts[0]));
        var format = GetQueryValue(request.Query, "format") ?? "json";
        if (format == "json") await WriteAgentJsonAsync(stream, value, cancellationToken: ct).ConfigureAwait(false);
        else if (format == "markdown")
        {
            var crash = value.Crash;
            var text = (crash is null ? "Crash report unavailable or not finalized.\n" :
                $"{(crash.Crashed ? "CRASHED" : "No native crash confirmed")} | {crash.Platform} | {crash.Code ?? "no fatal code"}\n" +
                $"Report {crash.Capture}; dump {crash.Dump}; analysis {crash.Analysis}.\n" +
                string.Join('\n', crash.TopFrames) + "\n" + string.Join('\n', crash.Issues) + "\n") +
                $"Diagnostic ZIP: {value.Bundle?.State ?? "pending"}; {value.Bundle?.Bytes ?? 0} bytes; mirror {value.Bundle?.MirrorState ?? "pending"}.\n" +
                $"Full report: {value.Report}\n";
            var bytes = Encoding.UTF8.GetBytes(text);
            await WriteHeadersAsync(stream, 200, "OK", new Dictionary<string, string>
            {
                ["Content-Type"] = "text/markdown; charset=utf-8", ["Content-Length"] = bytes.Length.ToString(CultureInfo.InvariantCulture), ["Cache-Control"] = "no-store"
            }, false, ct).ConfigureAwait(false);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        else throw new InvalidDataException("Crash report format must be json or markdown.");
        return false;
    }
}
