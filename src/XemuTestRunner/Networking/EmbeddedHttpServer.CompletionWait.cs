using System.Diagnostics;

namespace XemuTestRunner.Networking;

public sealed partial class EmbeddedHttpServer
{
    private sealed record CompletionState(string State, string? RunId, string? Code, string Detail)
    {
        public bool Terminal => State is "tested" or "cancelled" or "failed";
        public bool ShouldWait => !Terminal && Code is null;
    }

    // One bounded HTTP response, not an unbounded socket or another executor.
    // The caller can repeat this read silently until finished, or show heartbeats.
    private async Task<bool?> TryCompletionWaitRouteAsync(Stream stream, HttpRequest request, CancellationToken ct)
    {
        if (request.Method != "GET") return null;
        if (request.Path == "/api/v1/help" && GetQueryValue(request.Query, "topic") == "completion-wait")
        {
            await WriteAgentJsonAsync(stream, new
            {
                capability = "completionWait",
                requestedTest = "/api/v1/test-runs/{id}/wait?wait=20",
                apiJob = "/api/v1/jobs/{id}/wait?wait=20",
                defaultWaitSeconds = 20, maxWaitSeconds = 120,
                events = new[] { "heartbeat", "finished", "attention" },
                client = "runner_tests.py wait ID --follow [--updates] [--interval 20] [--job]",
                rule = "Repeat heartbeat reads with the SAME ID. Completion and blockers return early. Waiting/disconnecting never starts, retries or cancels work. Terminal does not mean passed.",
                capacity = "32 waiting requests shared with lifecycle observations; excess waits return 429."
            }, cancellationToken: ct).ConfigureAwait(false);
            return false;
        }

        const string jobs = "/api/v1/jobs/";
        const string tests = "/api/v1/test-runs/";
        var requested = request.Path.StartsWith(tests, StringComparison.Ordinal);
        var prefix = requested ? tests : jobs;
        if (!request.Path.StartsWith(prefix, StringComparison.Ordinal) ||
            !request.Path.EndsWith("/wait", StringComparison.Ordinal) ||
            request.Path.Length <= prefix.Length + 5) return null;
        var encoded = request.Path[prefix.Length..^5];
        if (encoded.Length == 0 || encoded.Contains('/')) return null;
        var id = Uri.UnescapeDataString(encoded);
        var seconds = ReadBoundedQuery(request.Query, "wait", 20, 0, 120);
        var value = ReadCompletionState(id, requested);

        if (seconds > 0 && value.ShouldWait)
        {
            // Reuse the existing admission bound; normal status/result requests
            // and immediately finished waits do not consume a waiting slot.
            if (!await _observationWaiters.WaitAsync(0, ct).ConfigureAwait(false))
                throw new AgentRequestException(429, "too_many_waiters", "The bounded waiter limit is reached.",
                    "Retry this read later with the same ID. Do not resubmit the test.");
            try
            {
                var clock = Stopwatch.StartNew();
                var limit = TimeSpan.FromSeconds(seconds);
                while (value.ShouldWait)
                {
                    var remaining = limit - clock.Elapsed;
                    if (remaining <= TimeSpan.Zero) break;
                    await Task.Delay(remaining < TimeSpan.FromMilliseconds(500) ? remaining : TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
                    value = ReadCompletionState(id, requested);
                }
            }
            finally { _observationWaiters.Release(); }
        }

        object? result = null;
        if (value.State == "tested")
        {
            if (value.RunId is null)
                result = new { available = false, code = "run_identity_missing", outcome = (object?)null };
            else
            {
                try { result = _assessmentReader.Read(_paths.Results, value.RunId); }
                catch (AgentRequestException error) when (error.Status == 404)
                { result = new { available = false, code = "run_evidence_missing", outcome = (object?)null }; }
            }
        }

        await WriteAgentJsonAsync(stream, new
        {
            ok = true, id, state = value.State,
            @event = value.Terminal ? "finished" : value.ShouldWait ? "heartbeat" : "attention",
            terminal = value.Terminal, runId = value.RunId, code = value.Code,
            next = value.ShouldWait ? prefix + Uri.EscapeDataString(id) + "/wait?wait=" + (seconds == 0 ? 20 : seconds) : value.Detail,
            result
        }, cancellationToken: ct).ConfigureAwait(false);
        return false;
    }

    private CompletionState ReadCompletionState(string id, bool requested)
    {
        var runner = _state.Snapshot();
        var detail = requested ? "/api/v1/test-runs/" + Uri.EscapeDataString(id) : AgentJobStore.Url(id) + "?view=summary";
        string state;
        string? runId;
        string? code;
        if (requested)
        {
            var selection = AgentJobs.ReadRequestedTest(id);
            state = selection.State;
            runId = selection.RunId;
            code = selection.ErrorCode;
            // Before materialization there is intentionally no API job. Once a
            // package exists, its owner is authoritative (including cancellation).
            if (state is "queuedForExecution" or "running" or "held" or "tested")
            {
                var job = AgentJobs.Observe(id, runner, _observationEpoch);
                state = job.State;
                runId = job.RunId;
                code = job.Blocker?.Code;
            }
            else if (selection.StartRequestedUtc is null && state != "cancelled")
                code = "start_required";
        }
        else
        {
            var job = AgentJobs.Observe(id, runner, _observationEpoch);
            state = job.State;
            runId = job.RunId;
            code = job.Blocker?.Code;
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
        if (code is null && (state is "queued" or "preparing") && runner.QueueIssue is not null)
            code = "queue_blocked";
        if (code?.Length > 96) code = code[..96];
        if (state == "tested" && runId is not null)
            detail = "/api/v1/runs/" + Uri.EscapeDataString(runId) + "?view=summary";
        return new(state, runId, code, detail);
    }
}
