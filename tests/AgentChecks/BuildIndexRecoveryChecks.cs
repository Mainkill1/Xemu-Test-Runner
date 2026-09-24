using System.Net;
using static AgentFixture;

internal static class BuildIndexRecoveryChecks
{
    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("corrupt archived metadata is reported without blocking explicitly requested tests", async () =>
        {
            await using var host = new AgentFixture();
            host.State.SetPhase("fixture");
            var revision = await RequestedTestChecks.Setup(host);
            await host.CreateDraft("broken-history");
            var archived = Path.Combine(host.Paths.Tested, "agent-broken-history");
            Directory.Move(host.Draft("broken-history"), archived);
            await File.WriteAllTextAsync(Path.Combine(archived, ".runner-attempt.json"), "{");
            host.State.SetPhase("idle");
            // A scheduled idle scan may not have run when an execution request
            // reaches the queue. Observe the indexer's result before asserting
            // diagnostics; do not make the test depend on a 250 ms race.
            var deadline = DateTime.UtcNow.AddSeconds(12);
            var reported = false;
            while (DateTime.UtcNow < deadline)
            {
                var status = await host.Json("/api/v1/build-results/index-status");
                if (status.GetProperty("issues").GetArrayLength() > 0)
                {
                    reported = true;
                    break;
                }
                await Task.Delay(100);
            }
            Require(reported, "Malformed archived evidence was silently ignored after the scheduled scan.");
            await host.Json("/api/v1/test-runs", HttpMethod.Post, new
            {
                id = "index-recovery", applicationJobId = "application", testId = "smoke", revision
            });
            await host.Json("/api/v1/test-runs/index-recovery/start", HttpMethod.Post, new { }, HttpStatusCode.Accepted);
            await RequestedTestChecks.Until(host, "index-recovery", "queuedForExecution");
            Require(await File.ReadAllTextAsync(Path.Combine(archived, ".runner-attempt.json")) == "{", "Index recovery modified archived evidence.");
        }));
    }
}
