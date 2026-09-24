using System.Diagnostics;
using XemuTestRunner.Runtime;

namespace XemuTestRunner.Networking;

public sealed partial class EmbeddedHttpServer
{
    private static readonly TimeSpan CompletionHeartbeat = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CompletionStateCheckInterval = TimeSpan.FromMilliseconds(500);

    private sealed record WaitQueueIssue(string Code, string Package, bool Retryable, bool HoldsTesting,
        DateTimeOffset DetectedUtc, bool Actionable);
    private sealed record CompletionState(string State, string? RunId, string? Code, string Detail, WaitQueueIssue? QueueIssue)
    {
        public bool Terminal => State is "tested" or "cancelled" or "failed";
        public bool ShouldWait => !Terminal && Code is null;
        public string Event => Terminal ? "finished" : ShouldWait ? "heartbeat" : "attention";
    }

    private async Task<bool?> TryCompletionWaitRouteAsync(Stream stream, HttpRequest request, CancellationToken ct)
    {
        if (request.Method != "GET") return null;
        if (request.Path == "/api/v1/help" && GetQueryValue(request.Query, "topic") == "completion-wait")
        {
            await WriteAgentJsonAsync(stream, new
            {
                capability = "completionWait", requestedTest = "/api/v1/test-runs/{id}/wait",
                apiJob = "/api/v1/jobs/{id}/wait", duration = "untilFinishedOrAttention", heartbeats = "automatic",
                events = new[] { "heartbeat", "finished", "attention" }, client = "runner_tests.py wait ID [--updates] [--job]",
                rule = "There is no caller-selected deadline. Follow heartbeat reads with the SAME ID. Waiting/disconnecting never starts, retries or cancels work. Terminal does not mean passed.",
                queue = "Normal package stabilization continues waiting; actionable blockage includes its original queue code and package identity.",
                capacity = "32 held requests shared with lifecycle observations; excess reads reconnect after 429."
            }, cancellationToken: ct).ConfigureAwait(false);
            return false;
        }
        const string jobsPrefix = "/api/v1/jobs/";
        const string requestedTestsPrefix = "/api/v1/test-runs/";
        const string waitSuffix = "/wait";
        var isRequestedTest = request.Path.StartsWith(requestedTestsPrefix, StringComparison.Ordinal);
        var prefix = isRequestedTest ? requestedTestsPrefix : jobsPrefix;
        if (!request.Path.StartsWith(prefix, StringComparison.Ordinal) || !request.Path.EndsWith(waitSuffix, StringComparison.Ordinal) ||
            request.Path.Length <= prefix.Length + waitSuffix.Length) return null;
        var encodedId = request.Path[prefix.Length..^waitSuffix.Length];
        if (encodedId.Length == 0 || encodedId.Contains('/')) return null;
        if (!string.IsNullOrEmpty(request.Query))
            throw new AgentRequestException(400, "wait_parameters_unsupported", "Completion waiting has no duration or interval parameters.",
                "Use the wait URL without a query. The runner supplies heartbeats; the client follows until completion.");
        var id = Uri.UnescapeDataString(encodedId);
        var completion = ReadCompletionState(id, isRequestedTest);
        completion = await WaitForCompletionOrHeartbeatAsync(id, isRequestedTest, completion, ct).ConfigureAwait(false);
        await WriteAgentJsonAsync(stream, new
        {
            ok = true, id, state = completion.State, @event = completion.Event, terminal = completion.Terminal,
            runId = completion.RunId, code = completion.Code, queueIssue = completion.QueueIssue,
            next = completion.ShouldWait ? prefix + Uri.EscapeDataString(id) + waitSuffix : completion.Detail,
            result = ReadCompletionAssessment(completion)
        }, cancellationToken: ct).ConfigureAwait(false);
        return false;
    }

    private async Task<CompletionState> WaitForCompletionOrHeartbeatAsync(string id, bool isRequestedTest,
        CompletionState completion, CancellationToken ct)
    {
        if (!completion.ShouldWait) return completion;
        if (!await _observationWaiters.WaitAsync(0, ct).ConfigureAwait(false))
            throw new AgentRequestException(429, "too_many_waiters", "The bounded waiter limit is reached.",
                "Reconnect this read with the same ID. Do not resubmit the test.");
        try
        {
            var clock = Stopwatch.StartNew();
            while (completion.ShouldWait)
            {
                var remaining = CompletionHeartbeat - clock.Elapsed;
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(remaining < CompletionStateCheckInterval ? remaining : CompletionStateCheckInterval, ct).ConfigureAwait(false);
                completion = ReadCompletionState(id, isRequestedTest);
            }
            return completion;
        }
        finally { _observationWaiters.Release(); }
    }

    private object? ReadCompletionAssessment(CompletionState completion)
    {
        if (completion.State != "tested") return null;
        if (completion.RunId is null) return new { available = false, code = "run_identity_missing", outcome = (object?)null };
        try { return _assessmentReader.Read(_paths.Results, completion.RunId); }
        catch (AgentRequestException error) when (error.Status == 404)
        { return new { available = false, code = "run_evidence_missing", outcome = (object?)null }; }
    }

    private CompletionState ReadCompletionState(string id, bool isRequestedTest)
    {
        var runner = _state.Snapshot();
        var detail = isRequestedTest ? "/api/v1/test-runs/" + Uri.EscapeDataString(id) : AgentJobStore.Url(id) + "?view=summary";
        string state;
        string? runId;
        string? code;
        if (isRequestedTest)
        {
            var selection = AgentJobs.ReadRequestedTest(id);
            state = selection.State; runId = selection.RunId; code = selection.ErrorCode;
            if (state is "queuedForExecution" or "running" or "held" or "tested")
            {
                var job = AgentJobs.Observe(id, runner, _observationEpoch);
                state = job.State; runId = job.RunId; code = job.Blocker?.Code;
            }
            else if (selection.StartRequestedUtc is null && state != "cancelled") code = "start_required";
        }
        else
        {
            var job = AgentJobs.Observe(id, runner, _observationEpoch);
            state = job.State; runId = job.RunId; code = job.Blocker?.Code;
        }
        code ??= state switch
        {
            "draft" or "uploaded" => "start_required",
            "waitingForUpload" => "application_incomplete",
            "held" => "attempt_held",
            "blocked" => "queue_blocked",
            "unavailable" => "job_unavailable",
            "tested" or "cancelled" or "failed" or "queued" or "busy" or "preparing" or "testing" or "running" => null,
            _ => "state_unrecognized"
        };
        if (code is null && state is ("queued" or "preparing") && runner.QueueNeedsAttention) code = "queue_blocked";
        if (code?.Length > 96) code = code[..96];
        if (state == "tested" && runId is not null) detail = "/api/v1/runs/" + Uri.EscapeDataString(runId) + "?view=summary";
        // Keep the issue from this same observation, not a later snapshot after
        // another job starts. Names are bounded; the full message stays in status.
        var issue = runner.QueueIssue;
        var brief = issue is null ? null : new WaitQueueIssue(ClipWait(issue.Code), ClipWait(issue.Package),
            issue.Retryable, issue.HoldsTesting, issue.DetectedUtc, runner.QueueNeedsAttention);
        return new(state, runId, code, detail, brief);
    }

    private static string ClipWait(string value) => value.Length > 96 ? value[..96] : value;
}
