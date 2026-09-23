using System.Net;
using static AgentFixture;

internal static class BuildIndexRecoveryChecks
{
    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("corrupt archived metadata cannot block explicitly requested tests", async () =>
        {
            await using var host = new AgentFixture();
            host.State.SetPhase("fixture");
            var revision = await RequestedTestChecks.Setup(host);
            await host.CreateDraft("broken-history");
            var archived = Path.Combine(host.Paths.Tested, "agent-broken-history");
            Directory.Move(host.Draft("broken-history"), archived);
            await File.WriteAllTextAsync(Path.Combine(archived, ".runner-attempt.json"), "{");
            await host.Json("/api/v1/test-runs", HttpMethod.Post, new
            {
                id = "index-recovery", applicationJobId = "application", testId = "smoke", revision
            });
            await host.Json("/api/v1/test-runs/index-recovery/start", HttpMethod.Post, new { }, HttpStatusCode.Accepted);
            host.State.SetPhase("idle");
            await RequestedTestChecks.Until(host, "index-recovery", "queuedForExecution");
            var status = await host.Json("/api/v1/build-results/index-status");
            Require(status.GetProperty("issues").GetArrayLength() > 0, "Malformed archived evidence was silently ignored.");
            Require(await File.ReadAllTextAsync(Path.Combine(archived, ".runner-attempt.json")) == "{", "Index recovery modified archived evidence.");
        }));
    }
}
