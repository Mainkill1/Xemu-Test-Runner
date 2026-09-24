using System.Net;
using XemuTestRunner.Config;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;
using XemuTestRunner.Runtime;
using static AgentFixture;

internal static class QueueHandoffChecks
{
    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("requested completion wait survives real queue stabilization between two attempts", async () =>
        {
            await using var host = new AgentFixture();
            var revision = await RequestedTestChecks.Setup(host);
            host.State.SetPhase("fixture");
            foreach (var id in new[] { "handoff-first", "handoff-second" })
            {
                await host.Json("/api/v1/test-runs", HttpMethod.Post, new
                { id, applicationJobId = "application", testId = "smoke", revision });
                await host.Json("/api/v1/test-runs/" + id + "/start", HttpMethod.Post, new { }, HttpStatusCode.Accepted);
                await host.CreateDraft(id);
                await File.WriteAllTextAsync(Path.Combine(host.Draft(id), "xemu.bin"), "fixture");
            }
            host.MoveToTesting("handoff-first", "handoff-a", "running");
            Directory.Move(host.Draft("handoff-second"), Path.Combine(host.Paths.Pending, "agent-handoff-second"));
            host.State.BeginJob("handoff-first", "handoff-a", Environment.ProcessId);
            var waiting = host.Json("/api/v1/test-runs/handoff-second/wait");
            await Task.Delay(100);
            Directory.Move(Path.Combine(host.Paths.Testing, "agent-handoff-first"), Path.Combine(host.Paths.Tested, "agent-handoff-first"));
            host.State.EndJob("completed");
            var queue = new JobQueue(new RunnerConfig { Queue = new QueueOptions { PackageStabilityMs = 100 } }, host.Paths);
            var stabilization = queue.TryClaimNext();
            Require(stabilization.Issue?.Code == "package_stabilizing", "Did not reproduce the actual queue producer.");
            host.State.SetQueueIssue(stabilization.Issue);
            await Task.Delay(650);
            Require(!waiting.IsCompleted, "Healthy stabilization incorrectly ended the indefinite wait.");
            var observation = await host.Json("/api/v1/jobs/handoff-second?view=summary");
            Require(observation.GetProperty("state").GetString() == "queued", "Expected progress became blocked.");
            var claimed = queue.TryClaimNext();
            Require(claimed.Package is not null && claimed.Issue is null, "The healthy queue did not advance.");
            AtomicJson.Write(Path.Combine(claimed.Package!, ".runner-attempt.json"), new { RunId = "handoff-b", Phase = "running", Attempt = 1 });
            host.State.BeginJob("handoff-second", "handoff-b", Environment.ProcessId);
            Require(host.State.Snapshot().QueueIssue is null, "The new owned attempt retained its predecessor's issue.");
            host.Assessment("handoff-b", new RunAssessment(ExecutionOutcome.Completed, CorrectnessOutcome.Passed,
                EvidenceOutcome.Complete, ComparisonEligibility.Eligible, [], []));
            Directory.Move(claimed.Package!, Path.Combine(host.Paths.Tested, "agent-handoff-second"));
            host.State.EndJob("completed");
            var final = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            Require(final.GetProperty("event").GetString() == "finished" && final.GetProperty("runId").GetString() == "handoff-b", "Wait lost the selected attempt.");
        }));
        checks.Add(("unmaterialized selections wait through handoff progress without caller timers", async () =>
        {
            await using var host = new AgentFixture();
            var revision = await RequestedTestChecks.Setup(host);
            host.State.SetPhase("fixture");
            await host.Json("/api/v1/test-runs", HttpMethod.Post, new { id = "later", applicationJobId = "application", testId = "smoke", revision });
            await host.Json("/api/v1/test-runs/later/start", HttpMethod.Post, new { }, HttpStatusCode.Accepted);
            host.State.SetQueueIssue(new QueueIssue("package_stabilizing", "agent-earlier", "Normal publication", DateTimeOffset.UtcNow, true, false));
            var waiting = host.Json("/api/v1/test-runs/later/wait");
            await Task.Delay(650);
            Require(!waiting.IsCompleted, "A prior package's progress ended the later selection wait.");
            await host.Json("/api/v1/test-runs/later", HttpMethod.Delete);
            Require((await waiting.WaitAsync(TimeSpan.FromSeconds(5))).GetProperty("state").GetString() == "cancelled", "Cancellation did not release wait.");
        }));
        checks.Add(("actionable queue blockage keeps its cause and package identity", async () =>
        {
            await using var host = new AgentFixture();
            await host.CreateDraft("blocked-later");
            Directory.Move(host.Draft("blocked-later"), Path.Combine(host.Paths.Pending, "agent-blocked-later"));
            foreach (var code in new[] { "package_access_denied", "package_busy", "package_claim_invalid" })
            {
                host.State.SetQueueIssue(new QueueIssue(code, "agent-bad-head", "Fix the queue head", DateTimeOffset.UtcNow, code == "package_busy", code == "package_claim_invalid"));
                var response = await host.Json("/api/v1/jobs/blocked-later/wait").WaitAsync(TimeSpan.FromSeconds(3));
                Require(response.GetProperty("event").GetString() == "attention", "Real actionable queue issue was hidden.");
                var issue = response.GetProperty("queueIssue");
                Require(issue.GetProperty("code").GetString() == code && issue.GetProperty("package").GetString() == "agent-bad-head", "Wait discarded diagnostic cause or package identity.");
            }
        }));
        checks.Add(("a stability-named condition that holds Testing is never ignored", async () =>
        {
            await using var host = new AgentFixture();
            await host.CreateDraft("held-head");
            Directory.Move(host.Draft("held-head"), Path.Combine(host.Paths.Pending, "agent-held-head"));
            host.State.SetQueueIssue(new QueueIssue("package_stabilizing", "agent-held-head", "Unexpected held state", DateTimeOffset.UtcNow, true, true));
            Require((await host.Json("/api/v1/jobs/held-head/wait").WaitAsync(TimeSpan.FromSeconds(3))).GetProperty("event").GetString() == "attention", "A Testing hold was masked by its name.");
        }));
    }
}
