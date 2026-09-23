using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Control;
using XemuTestRunner.Diagnostics;
using XemuTestRunner.Monitoring;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;
using XemuTestRunner.Runtime;

if (args.Length > 1 && args[0] == "--target")
{
    if (args[1] == "crash")
    {
        if (OperatingSystem.IsWindows()) Native.SetErrorMode(0x0002);
        Environment.FailFast("Intentional isolated crash fixture.");
    }
    if (args[1] == "hang") await Task.Delay(Timeout.Infinite);
    return args[1] == "exit139" ? 139 : 0;
}

var failures = 0;
var evidence = Environment.GetEnvironmentVariable("CRASH_CHECK_EVIDENCE") ?? Path.Combine(Path.GetTempPath(), "crash-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(evidence);
await Check("crash archives diagnostics beside executable and next queued test completes", async () =>
{
    var (paths, results, engine) = await Run("crash-then-clean", "crash", "clean");
    Require(results.Count == 2, "Crash stopped the next queued test.");
    var first = results.Single(value => value.GetProperty("job").GetString() == "job-0");
    Require(first.GetProperty("status").GetString() == "crashed", "Native crash was not classified separately.");
    Require(first.GetProperty("assessment").GetProperty("Execution").GetString() == "crashed", "Assessment lost the crash.");
    Require(results.Single(value => value.GetProperty("job").GetString() == "job-1").GetProperty("status").GetString() == "completed", "Success after crash was not recorded.");
    Require(Directory.GetDirectories(paths.Testing).Length == 0, "Crashed executable still occupies Testing.");
    Require(engine.State.Snapshot().QueueIssue is null, "Crash left the queue blocked.");
    var runId = first.GetProperty("runId").GetString()!;
    var zipPath = Path.Combine(paths.Results, runId, "diagnostics.zip");
    Require(File.Exists(zipPath), "No diagnostic ZIP was retained.");
    using var zip = ZipFile.OpenRead(zipPath);
    Require(zip.GetEntry("crash/report.json") is not null, "ZIP has no structured crash report.");
    Require(zip.GetEntry("result.json") is not null, "ZIP has no final result.");
    Require(zip.GetEntry("bundle-manifest.json") is not null, "ZIP has no inventory/omissions manifest.");
    Require(Directory.EnumerateFiles(paths.Tested, "*.diagnostics.zip", SearchOption.AllDirectories).Any(), "ZIP is not beside the archived executable.");
});
await Check("ordinary exit 139 is not misclassified as SIGSEGV", async () =>
{
    var (_, results, _) = await Run("ordinary-exit", "exit139", "clean");
    Require(results.Count == 2, "Failed process prevented the next test.");
    Require(results.Single(value => value.GetProperty("job").GetString() == "job-0").GetProperty("status").GetString() == "failed", "Exit-code arithmetic manufactured a native crash.");
});
await Check("timeout termination remains timeout and does not block later work", async () =>
{
    var (paths, results, _) = await Run("timeout-then-clean", "hang", "clean");
    Require(results.Count == 2, "Timed-out target blocked the next test.");
    Require(results.Single(value => value.GetProperty("job").GetString() == "job-0").GetProperty("status").GetString() == "timeout", "Runner kill was reclassified as a crash.");
    Require(Directory.GetDirectories(paths.Testing).Length == 0, "Stopped target retained queue ownership.");
});
Console.WriteLine($"Crash checks: {3 - failures}/3 passed. Evidence: {evidence}");
return failures == 0 ? 0 : 1;

async Task Check(string name, Func<Task> run)
{
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + error); }
}
void Require(bool value, string error) { if (!value) throw new InvalidOperationException(error); }
async Task<(RunnerPaths Paths, List<JsonElement> Results, RunnerEngine Engine)> Run(string name, params string[] modes)
{
    var root = Path.Combine(evidence, name);
    Directory.CreateDirectory(root);
    var config = new RunnerConfig
    {
        Workspace = "workspace",
        Queue = new QueueOptions { PackageStabilityMs = 50, ScanIntervalMs = 25 },
        Http = new HttpOptions { Enabled = false },
        Monitoring = new MonitoringOptions { Enabled = false },
        XemuControl = new XemuControlOptions { Enabled = false },
        Diagnostics = new DiagnosticsOptions { AutoHangBundle = false, AutoFailureBundle = false },
        Reliability = new ReliabilityOptions
        {
            Preflight = new PreflightOptions { MinimumFreeSpaceBytes = 0 }, ProcessExitTimeoutMs = 2000,
            PreserveTargetOnRunnerError = false, Watchdog = new WatchdogOptions { Enabled = false }
        }
    };
    var configPath = Path.Combine(root, "runner.json");
    await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config, ConfigLoader.JsonOptions));
    var (_, paths) = ConfigLoader.Load(configPath);
    var exe = Path.GetFileName(Environment.ProcessPath!);
    for (var i = 0; i < modes.Length; i++)
    {
        var package = Path.Combine(paths.Pending, "job-" + i);
        Directory.CreateDirectory(package);
        foreach (var source in Directory.EnumerateFiles(AppContext.BaseDirectory))
        {
            var target = Path.Combine(package, Path.GetFileName(source));
            File.Copy(source, target);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(target, File.GetUnixFileMode(source));
        }
        var job = new JobDefinition
        {
            Id = "job-" + i, Executable = exe, Arguments = ["--target", modes[i]], TimeoutSeconds = modes[i] == "hang" ? 1 : 15,
            Environment = new Dictionary<string, string> { ["DOTNET_DbgEnableMiniDump"] = "0", ["COMPlus_DbgEnableMiniDump"] = "0" }
        };
        await File.WriteAllTextAsync(Path.Combine(package, "job.json"), JsonSerializer.Serialize(job, ConfigLoader.JsonOptions));
    }
    var engine = new RunnerEngine(config, paths);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    await engine.RunAsync(true, modes.Length, timeout.Token);
    Require(!timeout.IsCancellationRequested, "Runner exceeded the queue continuation deadline.");
    var results = new List<JsonElement>();
    foreach (var path in Directory.EnumerateFiles(paths.Results, "result.json", SearchOption.AllDirectories).Where(path => Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path))) == Path.GetFileName(paths.Results)))
    {
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        results.Add(json.RootElement.Clone());
    }
    return (paths, results, engine);
}
internal static class Native
{
    [DllImport("kernel32.dll")] internal static extern uint SetErrorMode(uint mode);
}
