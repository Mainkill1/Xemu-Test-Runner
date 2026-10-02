using XemuTestRunner.Networking;
using XemuTestRunner.Runtime;
using System.Text.Json;
using XemuTestRunner.Reliability;

internal static class ReportChecks
{
    public static async Task Run(Func<string, Func<Task>, Task> check, Func<AgentFixture, Task<JsonElement>> setup)
    {
        await check("campaign report compares only its eight exact attempts and flags changes above one percent", () =>
        {
            var plan = Plan();
            var values = new[] { 100d, 98d, 98d, 100d, 98d, 100d, 100d, 98d };
            var records = plan.Attempts.Select((attempt, index) => new KeyValuePair<string, BuildRunRecord>(
                attempt.Id, Record(attempt, values[index]))).ToDictionary();
            var report = XisoCampaignReportBuilder.Build(plan, records);
            var row = report.Rows.Single(x => x.TestId == "cpu.direct" && x.Metric == "median_us");
            AgentFixture.Require(report.Status == "eligible" && report.AttemptCount == 8, "Complete balanced run was not eligible.");
            AgentFixture.Require(row.ImprovementPercent == 2 && row.AbbaImprovementPercent == 2 &&
                row.BaabImprovementPercent == 2 && row.OverOnePercent, "Balanced gain or one-percent flag is wrong.");
            var markdown = XisoCampaignReportFormatting.Markdown(report);
            AgentFixture.Require(markdown.Contains("cpu.direct", StringComparison.Ordinal) &&
                markdown.Contains("+2.00%", StringComparison.Ordinal) &&
                markdown.Contains("suite qualification `candidate`", StringComparison.Ordinal) &&
                markdown.Contains("Changes above 1%", StringComparison.Ordinal), "PR-ready report omitted the per-leaf gain.");
            var csv = XisoCampaignReportFormatting.Csv(report);
            AgentFixture.Require(csv.Contains("\"cpu.direct\",\"cpu\",\"median_us\"", StringComparison.Ordinal) &&
                csv.Contains("a1,a2,a3,a4,b1,b2,b3,b4", StringComparison.Ordinal) &&
                csv.Contains(",100,100,100,100,98,98,98,98,", StringComparison.Ordinal) &&
                csv.Contains(",2,2,2,true,false", StringComparison.Ordinal), "Full campaign CSV omitted exact-order measurements.");
            return Task.CompletedTask;
        });
        await check("unstarted campaign report does not invent test outcomes", async () =>
        {
            await using var host = new AgentFixture();
            await setup(host);
            await host.Json("/api/v1/xiso-campaigns", HttpMethod.Post,
                new { id = "unstarted-report", application = "application", referenceApplication = "application", categories = new[] { "shaders" } });
            var report = await host.Json("/api/v1/xiso-campaigns/unstarted-report/report");
            AgentFixture.Require(report.GetProperty("status").GetString() == "ineligible" &&
                report.GetProperty("rows").GetArrayLength() == 0 && report.GetProperty("blockers").GetArrayLength() > 0,
                "Unstarted campaign produced a performance claim.");
        });
        await check("campaign run lookup requires an exact durable index receipt", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "xiso-report-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new BuildResultStore(root);
                var attempt = Plan().Attempts[0];
                var record = Record(attempt, 100);
                store.Save(record);
                AgentFixture.Require(store.TryRun(record.RunId) is null, "Unindexed result was treated as published.");
                AtomicJson.Write(Path.Combine(root, ".build-results", ".indexed", record.RunId + ".json"),
                    new { record.RunId, record.Sha256 });
                var loaded = store.TryRun(record.RunId);
                AgentFixture.Require(loaded?.RunId == record.RunId && loaded.Sha256 == record.Sha256 &&
                    loaded.Metrics.Single().Value == 100, "Exact indexed record was not loaded.");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
            return Task.CompletedTask;
        });
        await check("missing or ineligible campaign attempts cannot publish a gain", () =>
        {
            var plan = Plan();
            var records = plan.Attempts.Take(7).ToDictionary(x => x.Id, x => Record(x, 90));
            var missing = XisoCampaignReportBuilder.Build(plan, records);
            AgentFixture.Require(missing.Status != "eligible" && missing.Rows.Length == 0 &&
                missing.Blockers.Any(x => x.Contains("missing", StringComparison.Ordinal)), "Missing eighth attempt published a numeric result.");
            records[plan.Attempts[7].Id] = Record(plan.Attempts[7], 90) with { Eligible = false };
            var failed = XisoCampaignReportBuilder.Build(plan, records);
            AgentFixture.Require(failed.Status != "eligible" && failed.Rows.Length == 0 &&
                failed.Blockers.Any(x => x.Contains("ineligible", StringComparison.Ordinal)), "Failed attempt published a gain.");
            return Task.CompletedTask;
        });
        await check("ABBA and BAAB disagreement is visible rather than a pooled gain", () =>
        {
            var plan = Plan();
            var values = new[] { 100d, 98d, 98d, 100d, 102d, 100d, 100d, 102d };
            var records = plan.Attempts.Select((attempt, index) => new KeyValuePair<string, BuildRunRecord>(
                attempt.Id, Record(attempt, values[index]))).ToDictionary();
            var report = XisoCampaignReportBuilder.Build(plan, records);
            var row = report.Rows.Single(x => x.Metric == "median_us");
            AgentFixture.Require(report.Status == "inconclusive" && row.OrderDisagreement &&
                row.AbbaImprovementPercent == 2 && row.BaabImprovementPercent == -2,
                "Opposite order effects were hidden by the pooled median.");
            return Task.CompletedTask;
        });
    }

    private static XisoCampaignPlan Plan()
    {
        var labels = new[] { "A1", "B1", "B2", "A2", "B3", "A3", "A4", "B4" };
        var attempts = labels.Select((label, index) => new XisoAttempt("attempt-" + index,
            label.StartsWith('A') ? "reference" : "candidate", "identity", 1, label,
            label.StartsWith('A') ? "reference" : "candidate")).ToArray();
        return new XisoCampaignPlan("campaign", "suite", "revision", new string('a', 64),
            "sha256:" + new string('b', 64), "candidate", "full", new XisoSettings().Resolve(),
            ["cpu.direct"], [], [new XisoChunk(1, "test", "revision", "plan", ["cpu.direct"], ["cpu"], new())], attempts);
    }

    private static BuildRunRecord Record(XisoAttempt attempt, double value) => new(
        attempt.Id, new string(attempt.Variant == "reference" ? 'a' : 'b', 64),
        "test-key", "environment-key", attempt.Variant + "-build", "test",
        new AgentOutcome("completed", "passed", "complete", "eligible"), true, [],
        [new BuildMetric("xiso/cpu/direct/median_us", value, "us", "lower")], "raw.csv");
}
