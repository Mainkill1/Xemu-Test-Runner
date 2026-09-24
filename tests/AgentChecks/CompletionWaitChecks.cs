using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using XemuTestRunner.Runtime;
using XemuTestRunner.Queue;
using static AgentFixture;

internal static class CompletionWaitChecks
{
    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("completion wait returns a small heartbeat without disturbing a benchmark", async () =>
        {
            await using var host = new AgentFixture();
            await host.CreateDraft("wait-running", 1000);
            host.MoveToTesting("wait-running", "run-wait", "running");
            host.State.BeginJob("wait-running", "run-wait", Environment.ProcessId,
                new OperationPolicyDefinition { Mode = "benchmark" });
            var clock = Stopwatch.StartNew();
            var value = await host.Json("/api/v1/jobs/wait-running/wait?wait=1");
            Require(clock.ElapsedMilliseconds >= 600 && clock.Elapsed < TimeSpan.FromSeconds(10), "Heartbeat interval was not respected.");
            Require(value.GetProperty("event").GetString() == "heartbeat" && !value.GetProperty("terminal").GetBoolean(), "Running job was declared finished.");
            Require(value.GetProperty("id").GetString() == "wait-running", "Heartbeat lost the attempt ID.");
            Require(value.GetProperty("result").ValueKind == JsonValueKind.Null, "Running heartbeat loaded evidence.");
            Require(Encoding.UTF8.GetByteCount(value.GetRawText()) <= 1024, "Heartbeat exceeded 1 KiB.");
            Require(!value.TryGetProperty("job", out _) && !value.TryGetProperty("files", out _), "Heartbeat echoed the plan or manifest.");
            Require(host.State.Snapshot().CurrentJob == "wait-running", "Waiting changed process ownership.");
        }));

        checks.Add(("completion wait stays open until archival and returns the crash assessment", async () =>
        {
            await using var host = new AgentFixture();
            await host.CreateDraft("wait-crash");
            host.MoveToTesting("wait-crash", "crashed-run", "running");
            var response = host.Json("/api/v1/jobs/wait-crash/wait?wait=20");
            await Task.Delay(150);
            Require(!response.IsCompleted, "Wait responded before completion or heartbeat.");
            host.Assessment("crashed-run", new RunAssessment(ExecutionOutcome.Crashed, CorrectnessOutcome.NotEvaluated,
                EvidenceOutcome.Incomplete, ComparisonEligibility.Ineligible, ["target_crashed"], []));
            Directory.Move(Path.Combine(host.Paths.Testing, "agent-wait-crash"), Path.Combine(host.Paths.Tested, "agent-wait-crash"));
            var value = await response.WaitAsync(TimeSpan.FromSeconds(5));
            Require(value.GetProperty("event").GetString() == "finished" && value.GetProperty("terminal").GetBoolean(), "Archived crash did not release the waiter.");
            Require(value.GetProperty("result").GetProperty("outcome").GetProperty("execution").GetString() == "crashed", "Crash was replaced with generic success.");
        }));

        checks.Add(("unstarted jobs and selections require attention without requesting execution", async () =>
        {
            await using var host = new AgentFixture();
            await host.CreateDraft("draft-wait");
            var draft = await host.Json("/api/v1/jobs/draft-wait/wait?wait=120");
            Require(draft.GetProperty("event").GetString() == "attention", "Unsubmitted draft was blindly polled.");
            Require(draft.GetProperty("code").GetString() == "start_required", "Missing start request was not explained.");
            var revision = await RequestedTestChecks.Setup(host);
            await host.Json("/api/v1/test-runs", HttpMethod.Post, new { id = "selected-wait", applicationJobId = "application", testId = "smoke", revision });
            var selected = await host.Json("/api/v1/test-runs/selected-wait/wait?wait=120");
            Require(selected.GetProperty("code").GetString() == "start_required", "Uploaded selection lost its explicit-start boundary.");
            var state = await host.Json("/api/v1/test-runs/selected-wait");
            Require(!state.GetProperty("startRequested").GetBoolean() && state.GetProperty("state").GetString() == "uploaded", "Waiting authorized a test.");
        }));

        checks.Add(("requested tests can be waited on before a materialized job exists", async () =>
        {
            await using var host = new AgentFixture();
            var revision = await RequestedTestChecks.Setup(host);
            host.State.BeginJob("other-test", "other-run", Environment.ProcessId, new OperationPolicyDefinition { Mode = "benchmark" });
            await host.Json("/api/v1/test-runs", HttpMethod.Post, new { id = "queued-wait", applicationJobId = "application", testId = "smoke", revision });
            await host.Json("/api/v1/test-runs/queued-wait/start", HttpMethod.Post, new { }, HttpStatusCode.Accepted);
            var value = await host.Json("/api/v1/test-runs/queued-wait/wait?wait=1");
            Require(value.GetProperty("event").GetString() == "heartbeat" && value.GetProperty("state").GetString() == "queued", "Queued request required a materialized job.");
            Require(!Directory.Exists(host.Draft("queued-wait")), "Waiting materialized a new job during the benchmark.");
            await host.Json("/api/v1/test-runs/queued-wait", HttpMethod.Delete);
            var cancelled = await host.Json("/api/v1/test-runs/queued-wait/wait?wait=120");
            Require(cancelled.GetProperty("terminal").GetBoolean() && cancelled.GetProperty("state").GetString() == "cancelled", "Cancellation did not release the wait.");
        }));

        checks.Add(("held attempts and queue blockers return attention immediately", async () =>
        {
            await using var host = new AgentFixture();
            await host.CreateDraft("held-wait");
            host.MoveToTesting("held-wait", "held-run", "held");
            var held = await host.Json("/api/v1/jobs/held-wait/wait?wait=120");
            Require(held.GetProperty("event").GetString() == "attention" && !held.GetProperty("terminal").GetBoolean(), "Held process was treated as completed.");
            await host.CreateDraft("blocked-wait");
            Directory.Move(host.Draft("blocked-wait"), Path.Combine(host.Paths.Pending, "agent-blocked-wait"));
            host.State.SetQueueIssue(new QueueIssue("blocked", "blocked-wait", "fixture", DateTimeOffset.UtcNow, Retryable: false, HoldsTesting: true));
            var blocked = await host.Json("/api/v1/jobs/blocked-wait/wait?wait=120");
            Require(blocked.GetProperty("event").GetString() == "attention", "Queue blockage was blindly polled.");
        }));

        checks.Add(("finished wait does not invent success when assessment is missing", async () =>
        {
            await using var host = new AgentFixture();
            await host.CreateDraft("missing-wait");
            host.MoveToTesting("missing-wait", "missing-result", "finalized");
            Directory.Move(Path.Combine(host.Paths.Testing, "agent-missing-wait"), Path.Combine(host.Paths.Tested, "agent-missing-wait"));
            Directory.CreateDirectory(Path.Combine(host.Paths.Results, "missing-result"));
            var value = await host.Json("/api/v1/jobs/missing-wait/wait?wait=120");
            var result = value.GetProperty("result");
            Require(value.GetProperty("terminal").GetBoolean() && !result.GetProperty("available").GetBoolean(), "Missing assessment became a passing test.");
            Require(result.GetProperty("outcome").ValueKind == JsonValueKind.Null, "Missing assessment manufactured outcomes.");
        }));

        checks.Add(("completion wait rejects invalid limits and nonexistent IDs", async () =>
        {
            await using var host = new AgentFixture();
            await host.CreateDraft("query-wait");
            foreach (var query in new[] { "-1", "121", "forever" })
                await host.Json("/api/v1/jobs/query-wait/wait?wait=" + query, expected: HttpStatusCode.BadRequest);
            await host.Json("/api/v1/jobs/not-there/wait?wait=0", expected: HttpStatusCode.NotFound);
            await host.Json("/api/v1/test-runs/not-there/wait?wait=0", expected: HttpStatusCode.NotFound);
            var help = await host.Json("/api/v1/help?topic=completion-wait");
            Require(help.GetProperty("maxWaitSeconds").GetInt32() == 120, "Completion bounds are not discoverable.");
        }));
    }
}
