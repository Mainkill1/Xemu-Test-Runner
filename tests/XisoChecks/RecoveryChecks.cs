using System.Net;
using System.Text.Json;
using static AgentFixture;

internal static class RecoveryChecks
{
    public static async Task Run(Func<string, Func<Task>, Task> check, Func<AgentFixture, Task<JsonElement>> setup)
    {
        await check("corrupt campaign history is visible without blocking valid requested work", async () =>
        {
            await using var host = new AgentFixture();
            await setup(host);
            var directory = Path.Combine(host.Paths.Pending, ".xiso-campaigns");
            Directory.CreateDirectory(directory);
            var corrupt = Path.Combine(directory, "broken.json");
            await File.WriteAllTextAsync(corrupt, "{");
            var listing = await host.Json("/api/v1/xiso-campaigns");
            Require(listing.GetProperty("issues").GetArrayLength() == 1, "Corrupt campaign history was silently omitted.");
            await host.Json("/api/v1/xiso-campaigns", HttpMethod.Post,
                new { id = "recoverable", application = "application", tests = new[] { "cpu.direct" } });
            await host.Json("/api/v1/xiso-campaigns/recoverable/start", HttpMethod.Post, new { }, HttpStatusCode.Accepted);
            host.State.SetPhase("idle");
            var deadline = DateTime.UtcNow.AddSeconds(12);
            while (!Directory.Exists(Path.Combine(host.Paths.Pending, "agent-xc-recoverable-001")) && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            Require(Directory.Exists(Path.Combine(host.Paths.Pending, "agent-xc-recoverable-001")), "Bad history blocked an unrelated valid campaign.");
            Require(await File.ReadAllTextAsync(corrupt) == "{", "Recovery rewrote corrupt evidence.");
        });
        await check("paired campaigns freeze ABBA then BAAB with identical chunks and durable attempt IDs", async () =>
        {
            await using var host = new AgentFixture();
            await setup(host);
            await host.Json("/api/v1/xiso-campaigns", HttpMethod.Post,
                new { id = "abba", application = "application", referenceApplication = "application", categories = new[] { "shaders" } });
            var plan = await host.Json("/api/v1/xiso-campaigns/abba?view=plan");
            var attempts = plan.GetProperty("attempts").EnumerateArray().ToArray();
            Require(attempts.Length == 16, "Two shader chunks require eight balanced attempts each.");
            var expected = new[] { "A1", "B1", "B2", "A2", "B3", "A3", "A4", "B4" };
            Require(attempts.Select(x => x.GetProperty("label").GetString()).SequenceEqual(expected.Concat(expected)), "ABBA/BAAB ordering changed.");
            Require(attempts.Select(x => x.GetProperty("id").GetString()).Distinct().Count() == 16, "Attempts share mutable identity.");
            Require(attempts.GroupBy(x => x.GetProperty("chunk").GetInt32()).All(group =>
                group.Select(x => x.GetProperty("application").GetString()).Distinct().Count() == 1),
                "Fixture application identity changed within a chunk.");
            var status = await host.Json("/api/v1/xiso-campaigns/abba");
            Require(!status.GetProperty("startRequested").GetBoolean() && status.GetProperty("comparisonEligible").GetInt32() == 0,
                "Planning implied execution or comparison qualification.");
        });
        await check("paired campaign defaults to the full pinned suite", async () =>
        {
            await using var host = new AgentFixture();
            await setup(host);
            var status = await host.Json("/api/v1/xiso-campaigns", HttpMethod.Post,
                new { id = "full-pair", application = "application", referenceApplication = "application" });
            var plan = await host.Json("/api/v1/xiso-campaigns/full-pair?view=plan");
            Require(status.GetProperty("selectedLeaves").GetInt32() == 9 && plan.GetProperty("mode").GetString() == "full",
                "Paired default selected a smoke subset instead of all nine leaves.");
            Require(plan.GetProperty("attempts").GetArrayLength() == plan.GetProperty("chunks").GetArrayLength() * 8,
                "A full paired campaign did not schedule both orders per chunk.");
        });
        await check("campaign stops materializing attempts after a setup failure", async () =>
        {
            await using var host = new AgentFixture(configurePreflight: options =>
                options.MinimumFreeSpaceBytes = long.MaxValue);
            await setup(host);
            await host.Json("/api/v1/xiso-campaigns", HttpMethod.Post,
                new { id = "setup-failure", application = "application", categories = new[] { "shaders" } });
            await host.Json("/api/v1/xiso-campaigns/setup-failure/start", HttpMethod.Post,
                new { }, HttpStatusCode.Accepted);
            host.State.SetPhase("idle");

            var deadline = DateTime.UtcNow.AddSeconds(12);
            JsonElement status = default;
            while (DateTime.UtcNow < deadline)
            {
                status = await host.Json("/api/v1/xiso-campaigns/setup-failure");
                if (status.GetProperty("terminal").GetBoolean()) break;
                await Task.Delay(100);
            }

            Require(status.GetProperty("terminal").GetBoolean(),
                "A setup failure did not terminate the campaign.");
            Require(status.GetProperty("finished").GetInt32() == 1 &&
                    status.GetProperty("failed").GetInt32() == 1,
                "The failed setup attempt was not retained exactly once.");
            Require(status.GetProperty("remaining").GetInt32() == 1,
                "The later attempt was materialized after setup had already failed.");
            Require(File.Exists(Path.Combine(host.Paths.Pending, ".requested-tests",
                        "xc-setup-failure-001.json")),
                "The first failed attempt was not retained for diagnosis.");
            Require(!File.Exists(Path.Combine(host.Paths.Pending, ".requested-tests",
                        "xc-setup-failure-002.json")),
                "The runner fanned out another package after setup failure.");
        });
    }
}
