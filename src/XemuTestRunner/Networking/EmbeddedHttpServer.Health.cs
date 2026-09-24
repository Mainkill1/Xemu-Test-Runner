using System.Runtime.InteropServices;
using System.Text.Json;

namespace XemuTestRunner.Networking;

public sealed partial class EmbeddedHttpServer
{
    // Health is deliberately projected only from in-memory runner state and
    // static process metadata. Polling it must not scan evidence, probe tools,
    // touch xemu, or disturb a benchmark.
    private async Task<bool?> TryHealthRouteAsync(
        Stream stream,
        HttpRequest request,
        bool keepAlive,
        CancellationToken ct)
    {
        if (request.Path is not ("/health" or "/api/v1/health") ||
            request.Method is not ("GET" or "HEAD"))
            return null;

        var snapshot = _state.Snapshot();
        var issue = snapshot.QueueIssue;
        var ready =
            !snapshot.Phase.Equals("starting", StringComparison.OrdinalIgnoreCase) &&
            issue is null;
        var status = ready ? "ok" : "degraded";

        object? active = snapshot.CurrentJob is null
            ? null
            : new
            {
                jobId = snapshot.CurrentJob,
                runId = snapshot.RunId,
                startedUtc = snapshot.JobStartedUtc
            };

        object? last =
            snapshot.LastJob is null &&
            snapshot.LastResult is null &&
            snapshot.LastFinishedUtc is null
                ? null
                : new
                {
                    jobId = snapshot.LastJob,
                    result = snapshot.LastResult,
                    finishedUtc = snapshot.LastFinishedUtc
                };

        object? compactIssue = issue is null
            ? null
            : new
            {
                code = issue.Code,
                retryable = issue.Retryable,
                holdsTesting = issue.HoldsTesting,
                detectedUtc = issue.DetectedUtc
            };

        var payload = new
        {
            schemaVersion = 1,
            service = "xemu-test-runner",
            version = ApplicationInfo.DisplayVersion,
            apiVersion = "v1",
            instance = _observationEpoch,
            status,
            ready,
            acceptingRequests = true,
            timestampUtc = DateTimeOffset.UtcNow,
            startedUtc = snapshot.StartedUtc,
            uptimeMs = snapshot.UptimeMs,
            platform = new
            {
                os = OperatingSystem.IsWindows() ? "windows" :
                    OperatingSystem.IsLinux() ? "linux" :
                    OperatingSystem.IsMacOS() ? "macos" : "other",
                architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()
            },
            phase = snapshot.Phase,
            active,
            queue = new
            {
                pending = snapshot.Queue.Pending,
                testing = snapshot.Queue.Testing,
                tested = snapshot.Queue.Tested,
                blocked = issue is not null
            },
            issue = compactIssue,
            last,
            stats = new
            {
                jobsFinished = snapshot.JobsFinished,
                failedJobs = snapshot.FailedJobs
            },
            checks = new
            {
                listener = "ok",
                queue = issue is null ? "ok" : "degraded"
            },
            links = new
            {
                status = "/api/v1/status",
                agent = "/api/v1/agent?view=summary",
                queue = "/api/v1/queue"
            }
        };

        var body = JsonSerializer.SerializeToUtf8Bytes(payload, AgentJson);
        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/json; charset=utf-8",
            ["Content-Length"] = body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Cache-Control"] = "no-store"
        };
        await WriteHeadersAsync(stream, 200, "OK", headers, keepAlive, ct).ConfigureAwait(false);
        if (request.Method == "GET")
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
        return keepAlive;
    }
}
