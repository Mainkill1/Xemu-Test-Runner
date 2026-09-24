using System.Text;
using System.Text.Json;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;
using XemuTestRunner.Runtime;
using static AgentFixture;

internal static class RunStateApiChecks
{
    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("state API reports paths and bounded inventories without downloading files", async () =>
        {
            await using var host = new AgentFixture();
            var result = Path.Combine(host.Paths.Results, "state-api");
            var (job, session) = await Prepare(host, result);
            await File.WriteAllTextAsync(Path.Combine(session.Report.CacheDirectory!, "shader.bin"), "compiled-shader");
            await session.CompleteAsync(true);
            host.State.BeginJob("benchmark", "active", Environment.ProcessId, new OperationPolicyDefinition { Mode = "benchmark" });
            var value = await host.Json("/api/v1/runs/state-api/state");
            Require(value.GetProperty("available").GetBoolean(), "State report is unavailable.");
            Require(value.GetProperty("mode").GetString() == "cold", "Wrong cache mode.");
            Require(value.GetProperty("after").GetProperty("fileCount").GetInt32() == 1, "Final cache inventory missing.");
            Require(value.GetProperty("paths").GetProperty("applicationCache").GetString() == session.Report.CacheDirectory, "Actual cache location was hidden.");
            Require(Encoding.UTF8.GetByteCount(value.GetRawText()) <= 4096, "State summary is too large.");
            Require(!value.GetProperty("after").TryGetProperty("files", out _), "Summary echoed the full file inventory.");
        }));
        checks.Add(("missing state is explicitly legacy not a clean cache", async () =>
        {
            await using var host = new AgentFixture();
            Directory.CreateDirectory(Path.Combine(host.Paths.Results, "legacy-state"));
            var value = await host.Json("/api/v1/runs/legacy-state/state");
            Require(!value.GetProperty("available").GetBoolean(), "Missing state was invented.");
            Require(!value.GetProperty("comparisonReady").GetBoolean(), "Legacy state was certified clean.");
        }));
        checks.Add(("benchmark missing a state policy receives a comparison blocker", async () =>
        {
            await using var host = new AgentFixture();
            var job = new JobDefinition { Id = "legacy", Executable = "fixture", Operations = new() { Mode = "benchmark" } };
            var value = await WorkloadEvaluator.EvaluateAsync(job, host.Paths.Tested, host.Paths.Results, null, 0, true, CancellationToken.None);
            Require(value.Checks.Any(check => check.Category == "state" && !check.Passed), "Unmanaged benchmark lacks a state blocker.");
            Require(value.Correctness == CorrectnessOutcome.NotEvaluated, "State control fabricated guest correctness.");
        }));
        checks.Add(("managed state needs actual evidence not just a requested configuration", async () =>
        {
            await using var host = new AgentFixture();
            var job = new JobDefinition { RuntimeState = new() { Isolation = new() { AllowUncontrolledDriverCache = true } } };
            var value = await WorkloadEvaluator.EvaluateAsync(job, host.Paths.Tested, host.Paths.Results, null, 0, true, CancellationToken.None);
            Require(value.Checks.Any(check => check.Category == "state" && !check.Passed), "Missing state report did not block comparison.");
        }));
        checks.Add(("explicit partial-control acceptance retains unresolved driver and OS scope", async () =>
        {
            await using var host = new AgentFixture();
            var result = Path.Combine(host.Paths.Results, "accepted-state");
            var (job, session) = await Prepare(host, result);
            await session.CompleteAsync(true);
            Require(session.Report.ComparisonReady, "Explicit partial-control profile did not qualify its recorded state.");
            Require(!session.Report.DriverNamespaceVerified && session.Report.Uncontrolled.Contains("driver-cache") && session.Report.Uncontrolled.Contains("os-page-cache"), "Acceptance falsely certified global caches as cold.");
            var value = await WorkloadEvaluator.EvaluateAsync(job, host.Paths.Tested, result, null, 0, true, CancellationToken.None);
            Require(value.Checks.Any(check => check.Category == "state" && check.Passed), "Actual state proof was not reflected in assessment.");
        }));
        checks.Add(("malformed state evidence cannot claim comparison readiness", async () =>
        {
            await using var host = new AgentFixture();
            var result = Path.Combine(host.Paths.Results, "invalid-state");
            Directory.CreateDirectory(Path.Combine(result, "diagnostics", "run-state"));
            await File.WriteAllTextAsync(Path.Combine(result, "diagnostics", "run-state", "report.json"), "{\"ComparisonReady\":true}");
            var value = await host.Json("/api/v1/runs/invalid-state/state");
            Require(!value.GetProperty("available").GetBoolean() && value.GetProperty("code").GetString() == "state_invalid", "Malformed state was treated as usable evidence.");
        }));
    }

    private static async Task<(JobDefinition, RunStorageSession)> Prepare(AgentFixture host, string result)
    {
        var package = Path.Combine(host.Root, "state-package");
        Directory.CreateDirectory(package); Directory.CreateDirectory(result);
        await File.WriteAllTextAsync(Path.Combine(package, "xemu.bin"), "fixture");
        await File.WriteAllTextAsync(Path.Combine(package, "seed-hdd"), "hdd");
        await File.WriteAllTextAsync(Path.Combine(package, "seed-eeprom"), "eeprom");
        await File.WriteAllTextAsync(Path.Combine(package, "xemu.toml"), "[perf]\ncache_shaders = true\n[sys.files]\nhdd_path = '{runtimeDir}/hdd.qcow2'\neeprom_path = '{runtimeDir}/eeprom.bin'\n");
        var job = new JobDefinition
        {
            Id = "state-fixture", Executable = "xemu.bin", Operations = new() { Mode = "benchmark" },
            RuntimeState = new()
            {
                Enabled = true,
                Isolation = new() { CacheMode = "cold", AllowUncontrolledDriverCache = true },
                Files = [new() { Source = "seed-hdd", Destination = "hdd.qcow2", ExpectedSha256 = Digest("hdd") },
                    new() { Source = "seed-eeprom", Destination = "eeprom.bin", ExpectedSha256 = Digest("eeprom") }]
            }
        };
        var runtime = await RuntimeStateManager.MaterializeAsync(job.RuntimeState, package, host.Root, Path.GetFileName(result), CancellationToken.None);
        AtomicJson.Write(Path.Combine(result, "runtime-state.json"), new { enabled = true, directory = runtime!.Directory, files = runtime.Files });
        var session = await RunStorageSession.PrepareAsync(job, Path.Combine(package, "xemu.bin"), package,
            [], new Dictionary<string, string>(), result, CancellationToken.None);
        return (job, session);
    }
}
