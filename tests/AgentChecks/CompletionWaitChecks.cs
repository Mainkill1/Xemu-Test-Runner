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
        checks.Add(("default wait provides automatic small heartbeats without a duration argument", async () =>
        {
            await using var host = new AgentFixture();
            await host.CreateDraft("wait-running", 1000);
            host.MoveToTesting("wait-running", "run-wait", "running");
            host.State.BeginJob("wait-running", "run-wait", Environment.ProcessId,
                new OperationPolicyDefinition { Mode = "benchmark" });
            var clock = Stopwatch.StartNew();
            var value = await host.Json("/api/v1/jobs/wait-running/wait");
            Require(clock.Elapsed >= TimeSpan.FromSeconds(18) && clock.Elapsed < TimeSpan.FromSeconds(29), "Automatic transport heartbeat was not held as expected.");
            Require(value.GetProperty("event").GetString() == "heartbeat" && !value.GetProperty("terminal").GetBoolean(), "Running job was declared finished.");
            Require(value.GetProperty("next").GetString() == "/api/v1/jobs/wait-running/wait", "Heartbeat requires an agent-selected timer.");
            Require(value.GetProperty("result").ValueKind == JsonValueKind.Null, "Heartbeat loaded raw evidence.");
            Require(Encoding.UTF8.GetByteCount(value.GetRawText()) <= 1024, "Heartbeat exceeded 1 KiB.");
            Require(host.State.Snapshot().CurrentJob == "wait-running", "Waiting changed ownership.");
        }));
        checks.Add(("default wait stays open until archival and returns the crash assessment", async () =>
        {
            await using var host = new AgentFixture();
            await host.CreateDraft("wait-crash");
            host.MoveToTesting("wait-crash", "crashed-run", "running");
            var response = host.Json("/api/v1/jobs/wait-crash/wait");
            await Task.Delay(150);
            Require(!response.IsCompleted, "Wait responded before completion.");
            host.Assessment("crashed-run", new RunAssessment(ExecutionOutcome.Crashed, CorrectnessOutcome.NotEvaluated,
                EvidenceOutcome.Incomplete, ComparisonEligibility.Ineligible, ["target_crashed"], []));
            Directory.Move(Path.Combine(host.Paths.Testing, "agent-wait-crash"), Path.Combine(host.Paths.Tested, "agent-wait-crash"));
            var value = await response.WaitAsync(TimeSpan.FromSeconds(5));
            Require(value.GetProperty("event").GetString() == "finished" && value.GetProperty("terminal").GetBoolean(), "Crash did not release the waiter.");
            Require(value.GetProperty("result").GetProperty("outcome").GetProperty("execution").GetString() == "crashed", "Crash became a pass.");
        }));
        checks.Add(("unstarted jobs and selections require attention without starting tests", async () =>
        {
            await using var host = new AgentFixture();
            await host.CreateDraft("draft-wait");
            var draft = await host.Json("/api/v1/jobs/draft-wait/wait");
            Require(draft.GetProperty("code").GetString() == "start_required", "Draft lost the start boundary.");
            var revision = await RequestedTestChecks.Setup(host);
            await host.Json("/api/v1/test-runs", HttpMethod.Post, new { id = "selected-wait", applicationJobId = "application", testId = "smoke", revision });
            var selected = await host.Json("/api/v1/test-runs/selected-wait/wait");
            Require(selected.GetProperty("event").GetString() == "attention", "Unstarted selection was blindly polled.");
            Require(!(await host.Json("/api/v1/test-runs/selected-wait")).GetProperty("startRequested").GetBoolean(), "Wait started a test.");
        }));
        checks.Add(("pre-materialization wait is released by explicit cancellation", async () =>
        {
            await using var host = new AgentFixture();
            var revision = await RequestedTestChecks.Setup(host);
            host.State.BeginJob("other-test", "other-run", Environment.ProcessId, new OperationPolicyDefinition { Mode = "benchmark" });
            await host.Json("/api/v1/test-runs", HttpMethod.Post, new { id = "queued-wait", applicationJobId = "application", testId = "smoke", revision });
            await host.Json("/api/v1/test-runs/queued-wait/start", HttpMethod.Post, new { }, HttpStatusCode.Accepted);
            var waiting = host.Json("/api/v1/test-runs/queued-wait/wait");
            await Task.Delay(150);
            Require(!waiting.IsCompleted && !Directory.Exists(host.Draft("queued-wait")), "Wait materialized a job or returned early.");
            await host.Json("/api/v1/test-runs/queued-wait", HttpMethod.Delete);
            var value = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            Require(value.GetProperty("terminal").GetBoolean() && value.GetProperty("state").GetString() == "cancelled", "Cancelled request did not release wait.");
        }));
        checks.Add(("held attempts and queue blockers return attention immediately", async () =>
        {
            await using var host = new AgentFixture();
            await host.CreateDraft("held-wait");
            host.MoveToTesting("held-wait", "held-run", "held");
            var held = await host.Json("/api/v1/jobs/held-wait/wait");
            Require(held.GetProperty("event").GetString() == "attention" && !held.GetProperty("terminal").GetBoolean(), "Held target was declared completed.");
            await host.CreateDraft("blocked-wait");
            Directory.Move(host.Draft("blocked-wait"), Path.Combine(host.Paths.Pending, "agent-blocked-wait"));
            host.State.SetQueueIssue(new QueueIssue("blocked", "blocked-wait", "fixture", DateTimeOffset.UtcNow, Retryable: false, HoldsTesting: true));
            Require((await host.Json("/api/v1/jobs/blocked-wait/wait")).GetProperty("event").GetString() == "attention", "Queue blockage was blindly polled.");
        }));
        checks.Add(("finished wait does not invent success when assessment is missing", async () =>
        {
            await using var host = new AgentFixture();
            await host.CreateDraft("missing-wait");
            host.MoveToTesting("missing-wait", "missing-result", "finalized");
            Directory.Move(Path.Combine(host.Paths.Testing, "agent-missing-wait"), Path.Combine(host.Paths.Tested, "agent-missing-wait"));
            Directory.CreateDirectory(Path.Combine(host.Paths.Results, "missing-result"));
            var result = (await host.Json("/api/v1/jobs/missing-wait/wait")).GetProperty("result");
            Require(!result.GetProperty("available").GetBoolean() && result.GetProperty("outcome").ValueKind == JsonValueKind.Null, "Missing assessment manufactured a pass.");
        }));
        checks.Add(("wait has no public timer options and unknown IDs remain errors", async () =>
        {
            await using var host = new AgentFixture();
            await host.CreateDraft("query-wait");
            foreach (var query in new[] { "wait=0", "wait=120", "timeout=60", "interval=1" })
                await host.Json("/api/v1/jobs/query-wait/wait?" + query, expected: HttpStatusCode.BadRequest);
            await host.Json("/api/v1/jobs/not-there/wait", expected: HttpStatusCode.NotFound);
            await host.Json("/api/v1/test-runs/not-there/wait", expected: HttpStatusCode.NotFound);
            var help = await host.Json("/api/v1/help?topic=completion-wait");
            Require(!help.TryGetProperty("maxWaitSeconds", out _) && !help.TryGetProperty("defaultWaitSeconds", out _), "Discovery asks agents to estimate duration.");
            Require(help.GetProperty("requestedTest").GetString() == "/api/v1/test-runs/{id}/wait", "Discovery advertises a timer.");
        }));
    }
}
