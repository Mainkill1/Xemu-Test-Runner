using System.Diagnostics;

namespace XemuTestRunner.Networking;

public sealed partial class EmbeddedHttpServer
{
    private readonly string _observationEpoch = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim _observationWaiters = new(32, 32);
    private readonly AgentAssessmentReader _assessmentReader = new();

    private async Task<bool?> TryObservationRouteAsync(Stream stream, HttpRequest request, CancellationToken ct)
    {
        if (request.Method != "GET") return null;
        var completion = await TryCompletionWaitRouteAsync(stream, request, ct).ConfigureAwait(false);
        if (completion.HasValue) return completion.Value;
        if (request.Path == "/api/v1/help" && GetQueryValue(request.Query, "topic") == "observations")
        {
            await WriteAgentJsonAsync(stream, new
            {
                topic = "observations",
                job = "/api/v1/jobs/{id}?view=summary&since={cursor}&wait=20",
                completion = "/api/v1/help?topic=completion-wait",
                result = "/api/v1/runs/{runId}?view=summary",
                state = "/api/v1/runs/{runId}/state",
                waitSeconds = new { minimum = 0, maximum = 20 },
                unchanged = "changed:false is a successful bounded wait, not failed submission.",
                identity = "Cursors identify lifecycle state and server instance, not plan revision.",
                resultContract = "ok is request success. Read execution, correctness, evidence and comparison independently. available:false never implies a pass.",
                detail = "/api/v1/help"
            }, cancellationToken: ct).ConfigureAwait(false);
            return false;
        }
        if (GetQueryValue(request.Query, "view") != "summary") return null;
        if (request.Path is "/api/v1" or "/api/v1/agent" or "/.well-known/agent.json")
        {
            var snapshot = _state.Snapshot();
            await WriteAgentJsonAsync(stream, new
            {
                api = "xemu-test-runner", protocol = "v1", agentRevision = 8,
                version = ApplicationInfo.DisplayVersion,
                instance = _observationEpoch, phase = snapshot.Phase,
                blocked = snapshot.QueueIssue is not null,
                bulkTransfersAllowed = snapshot.CurrentJob is null || snapshot.Operations.BulkTransfersAllowed,
                capabilities = new[] { "jobDrafts", "resumableUploads", "jobSummaries", "resultSummaries", "boundedWait", "pinnedTests", "payloadReuse", "artifactPages", "logCursors", "crashReports", "diagnosticZip", "runStateLedger", "diskAssets", "completionWait" },
                jobs = "/api/v1/jobs", runs = "/api/v1/runs", tests = "/api/v1/tests", diskAssets = "/api/v1/disk-assets",
                help = "/api/v1/help?topic=observations", testHelp = "/api/v1/help?topic=tests",
                waitHelp = "/api/v1/help?topic=completion-wait",
                stateHelp = "/api/v1/help?topic=run-state", diskAssetHelp = "/api/v1/help?topic=disk-assets",
                crashReadiness = "/api/v1/diagnostics/crash-capabilities",
                evidenceHelp = "/api/v1/help?topic=evidence", detail = "/api/v1/agent"
            }, cancellationToken: ct).ConfigureAwait(false);
            return false;
        }
        const string jobPrefix = "/api/v1/jobs/";
        if (request.Path.StartsWith(jobPrefix, StringComparison.Ordinal))
        {
            var id = request.Path[jobPrefix.Length..];
            if (id.Length == 0 || id.Contains('/')) return null;
            id = Uri.UnescapeDataString(id);
            var wait = ReadBoundedQuery(request.Query, "wait", 0, 0, 20);
            var since = GetQueryValue(request.Query, "since");
            if (since?.Length > 128) throw new InvalidDataException("since is too long.");
            var observation = AgentJobs.Observe(id, _state.Snapshot(), _observationEpoch);
            if (wait > 0 && since == observation.Cursor && observation.Blocker is null)
            {
                if (!await _observationWaiters.WaitAsync(0, ct).ConfigureAwait(false))
                    throw new AgentRequestException(429, "too_many_waiters", "The bounded waiter limit is reached.", "Retry this observation later with the same cursor.");
                try
                {
                    var timer = Stopwatch.StartNew();
                    while (timer.Elapsed < TimeSpan.FromSeconds(wait) && since == observation.Cursor && observation.Blocker is null)
                    {
                        var remaining = TimeSpan.FromSeconds(wait) - timer.Elapsed;
                        if (remaining <= TimeSpan.Zero) break;
                        await Task.Delay(remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
                        observation = AgentJobs.Observe(id, _state.Snapshot(), _observationEpoch);
                    }
                }
                finally { _observationWaiters.Release(); }
            }
            await WriteAgentJsonAsync(stream, observation with { Changed = since != observation.Cursor }, cancellationToken: ct).ConfigureAwait(false);
            return false;
        }
        const string runPrefix = "/api/v1/runs/";
        if (request.Path.StartsWith(runPrefix, StringComparison.Ordinal))
        {
            var id = request.Path[runPrefix.Length..];
            if (id.Length == 0 || id.Contains('/')) return null;
            var result = _assessmentReader.Read(_paths.Results, Uri.UnescapeDataString(id));
            await WriteAgentJsonAsync(stream, result, cancellationToken: ct).ConfigureAwait(false);
            return false;
        }
        return null;
    }
}
