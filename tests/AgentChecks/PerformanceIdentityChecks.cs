using XemuTestRunner.Reliability;
using static AgentFixture;

internal static class PerformanceIdentityChecks
{
    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("compact performance report distinguishes verified emulator and launcher hashes", async () =>
        {
            await using var host = new AgentFixture();
            await PerformanceAnalysisChecks.Analyze(host);
            var path = Path.Combine(host.Paths.Results, "analysis-run", "input-manifest.json");
            foreach (var verified in new[] { true, false })
            {
                AtomicJson.Write(path, new
                {
                    ExecutableSha256 = new string('a', 64),
                    Inputs = new[] { new { Path = "xemu", Role = "emulator", Sha256 = new string('b', 64), ExpectedSha256 = new string('b', 64), Verified = verified } }
                });
                var result = await host.Json("/api/v1/runs/analysis-run/performance");
                Require(result.GetProperty("launchExecutableSha256").GetString() == new string('a', 64), "Launch evidence was lost.");
                Require(result.GetProperty("executableSha256").GetString() == (verified ? new string('b', 64) : null), "Unverified native identity or launcher was labeled as the measured emulator.");
                if (!verified) Require(result.GetProperty("identityCode").GetString() == "emulator_identity_unverified", "Missing identity has no explanation.");
            }
        }));
    }
}
