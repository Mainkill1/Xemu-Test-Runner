using System.Text.Json;
using XemuTestRunner.Reliability;
using XemuTestRunner.Runtime;

namespace XemuTestRunner.Networking;

public sealed partial class EmbeddedHttpServer
{
    private readonly object _stateReportGate = new();
    private readonly Dictionary<string, (long Size, long Modified, object Summary)> _stateReports = new(StringComparer.Ordinal);

    private async Task<bool?> TryRunStateRouteAsync(Stream stream, HttpRequest request, CancellationToken ct)
    {
        if (request.Method != "GET") return null;
        if (request.Path == "/api/v1/help" && GetQueryValue(request.Query, "topic") == "run-state")
        {
            await WriteAgentJsonAsync(stream, new { capability = "runStateLedger", summary = "/api/v1/runs/{id}/state",
                policy = "RuntimeState.Isolation", modes = new[] { "cold", "seeded", "inherited" },
                scope = "xemu application cache, explicit config and private guest input identities; OS page cache and unverified driver state stay disclosed",
                detail = "The full state manifest and config snapshots are included in the diagnostic ZIP; reading state never starts a test." }, cancellationToken: ct).ConfigureAwait(false);
            return false;
        }
        const string prefix = "/api/v1/runs/";
        if (!request.Path.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var parts = request.Path[prefix.Length..].Split('/');
        if (parts.Length != 2 || parts[1] != "state") return null;
        var id = Uri.UnescapeDataString(parts[0]);
        var catalog = new EvidenceCatalog(_paths.Results);
        var root = catalog.Resolve(id, ".");
        if (!Directory.Exists(root)) throw new AgentRequestException(404, "run_not_found", "No run has this ID.", "Follow an existing run link.");
        var path = catalog.Resolve(id, "diagnostics/run-state/report.json");
        var file = new FileInfo(path);
        object value;
        if (!file.Exists) value = new { runId = id, available = false, comparisonReady = false, code = "state_not_recorded", mode = "legacy" };
        else
        {
            lock (_stateReportGate)
            {
                if (_stateReports.TryGetValue(id, out var cached) && cached.Size == file.Length && cached.Modified == file.LastWriteTimeUtc.Ticks)
                    value = cached.Summary;
                else
                {
                    try
                    {
                        var report = RunStateQualification.Read(root)!;
                        object? Brief(RunStateSnapshot? snapshot) => snapshot is null ? null : new
                        { snapshot.Complete, fileCount = snapshot.Files.Count, snapshot.Bytes, snapshot.TreeSha256 };
                        string? Clip(string? text) => text is null || text.Length <= 256 ? text : text[..253] + "...";
                        value = new
                        {
                            runId = id, available = true, report.Status, mode = report.CacheMode, report.CacheShaders,
                            report.ComparisonReady, report.ContractSha256,
                            driver = new { policy = report.DriverCache, verified = report.DriverNamespaceVerified, explicitlyAccepted = report.AllowUncontrolledDriverCache },
                            paths = new { applicationCache = Clip(report.CacheDirectory), priorCache = Clip(report.PriorCacheDirectory),
                                driverCache = Clip(report.DriverCacheDirectory), effectiveConfig = Clip(report.EffectiveConfigPath),
                                openGlShaders = report.StoragePaths.GetValueOrDefault("openglShaders"), openGlReloadList = report.StoragePaths.GetValueOrDefault("openglReloadList") },
                            before = Brief(report.Before), after = Brief(report.After), report.ConfigSha256,
                            report.EffectiveConfigSha256, report.ConfigAfterSha256,
                            uncontrolled = report.Uncontrolled.Take(8).ToArray(), issues = report.Issues.Take(8).ToArray(),
                            detail = "/api/v1/runs/" + Uri.EscapeDataString(id) + "/artifacts/diagnostics/run-state/report.json",
                            bundle = "/api/v1/runs/" + Uri.EscapeDataString(id) + "/diagnostics"
                        };
                    }
                    catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
                    { value = new { runId = id, available = false, comparisonReady = false, code = "state_invalid" }; }
                    if (_stateReports.Count >= 64) _stateReports.Clear();
                    _stateReports[id] = (file.Length, file.LastWriteTimeUtc.Ticks, value);
                }
            }
        }
        await WriteAgentJsonAsync(stream, value, cancellationToken: ct).ConfigureAwait(false);
        return false;
    }
}
