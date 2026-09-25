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
        await check("ABBA freezes identical chunk settings and distinct durable attempt IDs", async () =>
        {
            await using var host = new AgentFixture();
            await setup(host);
            await host.Json("/api/v1/xiso-campaigns", HttpMethod.Post,
                new { id = "abba", application = "application", referenceApplication = "application", categories = new[] { "shaders" } });
            var plan = await host.Json("/api/v1/xiso-campaigns/abba?view=plan");
            var attempts = plan.GetProperty("attempts").EnumerateArray().ToArray();
            Require(attempts.Length == 8, "Two shader chunks require eight ABBA attempts.");
            Require(attempts.Select(x => x.GetProperty("label").GetString()).SequenceEqual(new[] { "A1", "B1", "B2", "A2", "A1", "B1", "B2", "A2" }), "ABBA ordering changed.");
            Require(attempts.Select(x => x.GetProperty("id").GetString()).Distinct().Count() == 8, "Attempts share mutable identity.");
            var status = await host.Json("/api/v1/xiso-campaigns/abba");
            Require(!status.GetProperty("startRequested").GetBoolean() && status.GetProperty("comparisonEligible").GetInt32() == 0,
                "Planning implied execution or comparison qualification.");
        });
    }
}
