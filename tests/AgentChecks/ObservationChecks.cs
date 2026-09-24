using System.Diagnostics;
using System.Text;
using System.Text.Json;
using XemuTestRunner.Queue;
using XemuTestRunner.Runtime;
using static AgentFixture;

internal static class ObservationChecks
{
    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("discovery has a small capability entry view", async () =>
        {
            await using var host = new AgentFixture();
            var response = await host.Json("/api/v1/agent?view=summary");
            Require(response.TryGetProperty("capabilities", out var capabilities), "Compact discovery needs capabilities.");
            Require(capabilities.EnumerateArray().Any(x => x.GetString() == "jobSummaries"), "Summary support was not advertised.");
            Require(!response.TryGetProperty("routes", out _), "Entry view repeated the route manual.");
            Require(Encoding.UTF8.GetByteCount(response.GetRawText()) <= 2048, "Discovery exceeds the 2 KiB budget.");
        }));
        checks.Add(("job observations omit plans and manifests", async () =>
        {
            await using var host = new AgentFixture();
            await host.CreateDraft("large-plan", 1000);
            var response = await host.Json("/api/v1/jobs/large-plan?view=summary");
            Require(!response.TryGetProperty("job", out _) && !response.TryGetProperty("files", out _), "Status echoed the plan or manifest.");
            Require(response.GetProperty("id").GetString() == "large-plan", "Wrong job identity.");
            Require(response.GetProperty("state").GetString() == "draft", "Wrong ownership state.");
            Require(Encoding.UTF8.GetByteCount(response.GetRawText()) <= 1024, "Observation exceeds the 1 KiB budget.");
            var detail = await host.Json("/api/v1/jobs/large-plan");
            Require(detail.TryGetProperty("job", out _), "Legacy detailed contract was removed.");
        }));
        checks.Add(("lifecycle cursor changes without a plan edit", async () =>
        {
            await using var host = new AgentFixture();
            await host.CreateDraft("cursor");
            var first = await host.Json("/api/v1/jobs/cursor?view=summary");
            var again = await host.Json("/api/v1/jobs/cursor?view=summary");
            Require(first.GetProperty("cursor").GetString() == again.GetProperty("cursor").GetString(), "Unchanged state changed its cursor.");
            Directory.Move(host.Draft("cursor"), Path.Combine(host.Paths.Pending, "agent-cursor"));
            var queued = await host.Json("/api/v1/jobs/cursor?view=summary");
            Require(queued.GetProperty("state").GetString() == "queued", "Queue transition missing.");
            Require(first.GetProperty("cursor").GetString() != queued.GetProperty("cursor").GetString(), "Plan hash was reused as a lifecycle cursor.");
        }));
        checks.Add(("held attempts stop blind waiting", async () =>
        {
            await using var host = new AgentFixture();
            await host.CreateDraft("held");
            host.MoveToTesting("held", "held-run", "held");
            var response = await host.Json("/api/v1/jobs/held?view=summary");
            Require(response.GetProperty("state").GetString() == "held", "Held ownership was reported as ordinary testing.");
            Require(response.GetProperty("blocker").ValueKind == JsonValueKind.Object, "Held observation has no blocker.");
        }));
        checks.Add(("bounded wait returns unchanged without failing the job", async () =>
        {
            await using var host = new AgentFixture();
            await host.CreateDraft("wait");
            var first = await host.Json("/api/v1/jobs/wait?view=summary");
            var timer = Stopwatch.StartNew();
            var response = await host.Json("/api/v1/jobs/wait?view=summary&wait=1&since=" + Uri.EscapeDataString(first.GetProperty("cursor").GetString()!));
            Require(!response.GetProperty("changed").GetBoolean(), "No change was falsely reported.");
            Require(timer.ElapsedMilliseconds >= 600 && timer.Elapsed < TimeSpan.FromSeconds(10), "Wait was not bounded as requested.");
            Require(response.GetProperty("state").GetString() == "draft", "Waiting changed ownership.");
        }));
        checks.Add(("result summary preserves failed correctness", async () =>
        {
            await using var host = new AgentFixture();
            host.Assessment("failed", new RunAssessment(ExecutionOutcome.Completed, CorrectnessOutcome.Failed,
                EvidenceOutcome.Complete, ComparisonEligibility.Ineligible, ["correctness_failed"],
                [new AssessmentCheck("guest", false, "correctness", "Guest assertion failed.")]));
            var response = await host.Json("/api/v1/runs/failed?view=summary");
            var outcome = response.GetProperty("outcome");
            Require(outcome.GetProperty("execution").GetString() == "completed", "Execution outcome changed.");
            Require(outcome.GetProperty("correctness").GetString() == "failed", "Zero exit/finished was promoted to correctness.");
            Require(outcome.GetProperty("comparison").GetString() == "ineligible", "Ineligible attempt became eligible.");
            Require(!response.TryGetProperty("artifacts", out _), "Summary enumerated artifacts.");
        }));
        checks.Add(("malformed or absent assessments never imply pass", async () =>
        {
            await using var host = new AgentFixture();
            Directory.CreateDirectory(Path.Combine(host.Paths.Results, "missing"));
            var missing = await host.Json("/api/v1/runs/missing?view=summary");
            Require(!missing.GetProperty("available").GetBoolean(), "Missing assessment is available.");
            Require(missing.GetProperty("outcome").ValueKind == JsonValueKind.Null, "Missing assessment manufactured outcomes.");
            await File.WriteAllTextAsync(Path.Combine(host.Paths.Results, "missing", "assessment.json"), "{}");
            var malformed = await host.Json("/api/v1/runs/missing?view=summary");
            Require(!malformed.GetProperty("available").GetBoolean(), "Empty object became a valid assessment.");
            Require(malformed.GetProperty("code").GetString() == "assessment_invalid", "Malformed evidence was not explained.");
        }));
        checks.Add(("result reasons and checks are bounded with omission counts", async () =>
        {
            await using var host = new AgentFixture();
            host.Assessment("verbose", new RunAssessment(ExecutionOutcome.Completed, CorrectnessOutcome.Failed,
                EvidenceOutcome.Incomplete, ComparisonEligibility.Ineligible,
                Enumerable.Repeat(new string('r', 2000), 30).ToArray(),
                Enumerable.Range(0, 30).Select(i => new AssessmentCheck("check-" + i, false, "correctness", new string('d', 2000))).ToArray()));
            var response = await host.Json("/api/v1/runs/verbose?view=summary");
            Require(Encoding.UTF8.GetByteCount(response.GetRawText()) <= 4096, "Result exceeds the 4 KiB budget.");
            Require(response.GetProperty("moreReasons").GetInt32() > 0 && response.GetProperty("moreFailures").GetInt32() > 0, "Omitted details were hidden.");
        }));
        checks.Add(("small result observations remain usable during benchmark policy", async () =>
        {
            await using var host = new AgentFixture();
            host.Assessment("previous", new RunAssessment(ExecutionOutcome.Completed, CorrectnessOutcome.NotEvaluated,
                EvidenceOutcome.NotEvaluated, ComparisonEligibility.NotEvaluated, [], []));
            host.State.BeginJob("benchmark", "active", Environment.ProcessId, new OperationPolicyDefinition { Mode = "benchmark" });
            var response = await host.Json("/api/v1/runs/previous?view=summary");
            Require(response.GetProperty("available").GetBoolean(), "Compact assessment was treated as bulk transfer.");
            using var raw = await host.Client.GetAsync("/api/v1/runs/previous/artifacts/assessment.json");
            Require(raw.StatusCode == System.Net.HttpStatusCode.Conflict, "Raw transfer bypassed benchmark policy.");
        }));
    }
}
