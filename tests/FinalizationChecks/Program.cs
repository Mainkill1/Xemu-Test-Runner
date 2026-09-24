using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Diagnostics;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;
using XemuTestRunner.Runtime;

if (args.Length > 0 && args[0] == "--capture")
{
    await File.AppendAllTextAsync(args[2], "capture\n");
    await File.WriteAllBytesAsync(args[1], Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg=="));
    return 0;
}
if (args.Length > 0 && args[0] == "--target")
{
    var option = Array.IndexOf(args, "-qmp");
    var address = args[option + 1].Split(',')[0];
    var port = int.Parse(address[(address.LastIndexOf(':') + 1)..]);
    var listener = new TcpListener(IPAddress.Loopback, port);
    listener.Start();
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    try
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(limit.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync("{\"QMP\":{\"version\":{\"qemu\":{\"major\":9,\"minor\":0,\"micro\":0},\"package\":\"fixture\"},\"capabilities\":[]}}");
            while (await reader.ReadLineAsync(limit.Token) is { } line)
            {
                using var command = JsonDocument.Parse(line);
                var name = command.RootElement.GetProperty("execute").GetString();
                object result = name == "query-status" ? new { running = true, status = "running" } : new { };
                var id = command.RootElement.TryGetProperty("id", out var selectedId) ? selectedId.Clone() : default;
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { @return = result, id = id.ValueKind == JsonValueKind.Undefined ? (object?)null : id }));
                if (name == "quit") return 0;
            }
        }
    }
    finally { listener.Stop(); }
}

var failures = 0;
var checks = 0;
var root = Path.Combine(Path.GetTempPath(), "finalization-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
await Check("completed status forbids both failure capture paths even with a stale live handle", () =>
{
    var method = typeof(RunnerEngine).GetMethod("ShouldCaptureFailure", BindingFlags.NonPublic | BindingFlags.Static);
    Require(method is not null, "Shared failure-capture predicate is missing.");
    foreach (var exited in new[] { false, true })
    {
        var calls = 0;
        if ((bool)method!.Invoke(null, new object[] { "completed", false, exited })!) calls++;
        Require(calls == 0, "Completed run could invoke a failure screenshot command.");
    }
    foreach (var status in new[] { "cancelled", "unresponsive" })
        Require(!(bool)method!.Invoke(null, new object[] { status, false, false })!, status + " changed capture behavior.");
    foreach (var status in new[] { "control_error", "plan_failed", "runner_error" })
    {
        Require((bool)method!.Invoke(null, new object[] { status, false, false })!, "Live failure lost capture.");
        Require(!(bool)method.Invoke(null, new object[] { status, false, true })!, "Stopped target was captured.");
        Require(!(bool)method.Invoke(null, new object[] { status, true, false })!, "Preserved target was captured implicitly.");
    }
    return Task.CompletedTask;
});
await Check("a confirmed native or earlier exit cannot be undone by a stale process handle", () =>
{
    var method = typeof(RunnerEngine).GetMethod("ConfirmTargetExit", BindingFlags.NonPublic | BindingFlags.Static);
    Require(method is not null, "Monotonic exit reconciliation is missing.");
    foreach (var prior in new[] { false, true })
    foreach (var current in new[] { false, true })
    foreach (var native in new[] { false, true })
        Require((bool)method!.Invoke(null, new object[] { prior, current, native })! == (prior || current || native), "Exit evidence was discarded or invented.");
    return Task.CompletedTask;
});
await Check("clean QMP quit never captures failure or claims runner termination", async () =>
{
    for (var repetition = 0; repetition < 3; repetition++)
    {
        var (result, directory, captures) = await Run("clean-" + repetition, "clean");
        Require(result.GetProperty("status").GetString() == "completed", result.GetRawText());
        Require(result.GetProperty("exitCode").GetInt32() == 0, "Clean quit lost its exit code.");
        Require(!File.Exists(captures), "Successful run invoked the external screenshot provider.");
        Require(!File.Exists(Path.Combine(directory, "screenshots", "failure.png")), "Successful run retained a failure screenshot.");
        var crash = result.GetProperty("crash");
        Require(!crash.GetProperty("RunnerTerminated").GetBoolean(), "Clean native exit was mislabeled runner-terminated.");
        Require(!crash.GetProperty("Crashed").GetBoolean(), "Clean quit became a crash.");
        if (OperatingSystem.IsLinux())
        {
            using var native = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "crash", "native", "exit-native.json")));
            Require(native.RootElement.GetProperty("exitCode").GetInt32() == 0 && native.RootElement.GetProperty("signal").ValueKind == JsonValueKind.Null, "Native receipt disagrees with clean execution.");
            Require(native.RootElement.GetProperty("pid").GetInt32() == result.GetProperty("processId").GetInt32(), "Native receipt identifies a different process.");
        }
    }
});
await Check("live control failure still captures the fallback diagnostic", async () =>
{
    var (result, directory, captures) = await Run("control-error", "control-error");
    Require(result.GetProperty("status").GetString() == "control_error", result.GetRawText());
    Require(File.Exists(captures), "Live control failure did not invoke the configured screenshot provider.");
    Require(File.Exists(Path.Combine(directory, "screenshots", "failure.png")), "Live failure diagnostic was lost.");
});
await Check("live plan failure still captures the fallback diagnostic", async () =>
{
    var (result, directory, captures) = await Run("plan-error", "plan-error");
    Require(result.GetProperty("status").GetString() == "plan_failed", result.GetRawText());
    Require(File.Exists(captures) && File.Exists(Path.Combine(directory, "screenshots", "failure.png")), "Live plan failure lost the fallback screenshot.");
});
await Check("explicit cancellation does not add a failure-labeled screenshot", async () =>
{
    var (result, directory, captures) = await Run("cancel", "cancel");
    Require(result.GetProperty("status").GetString() == "cancelled", result.GetRawText());
    Require(!File.Exists(captures) && !File.Exists(Path.Combine(directory, "screenshots", "failure.png")), "Cancellation added a failure capture.");
});
Console.WriteLine($"Finalization checks: {checks - failures}/{checks} passed.");
try { Directory.Delete(root, true); } catch (IOException) { }
return failures == 0 ? 0 : 1;

async Task Check(string name, Func<Task> run)
{
    checks++;
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + error); }
}
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
async Task<(JsonElement Result, string Directory, string Captures)> Run(string name, string mode)
{
    var folder = Path.Combine(root, name);
    Directory.CreateDirectory(folder);
    var captures = Path.Combine(folder, "capture-invocations.txt");
    var config = new RunnerConfig
    {
        Workspace = "workspace",
        Queue = new QueueOptions { PackageStabilityMs = 100, ScanIntervalMs = 25 },
        Http = new HttpOptions { Enabled = false },
        Monitoring = new MonitoringOptions { Enabled = false },
        XemuControl = new XemuControlOptions { Enabled = true, InputProvider = "none", ScreenshotProvider = "external",
            ScreenshotExecutable = Environment.ProcessPath!, ScreenshotArguments = ["--capture", "{path}", captures], ScreenshotTimeoutMs = 3000 },
        Diagnostics = new DiagnosticsOptions { AutoFailureBundle = false, AutoHangBundle = false },
        Reliability = new ReliabilityOptions { PreserveTargetOnRunnerError = false, ProcessExitTimeoutMs = 3000,
            Preflight = new PreflightOptions { MinimumFreeSpaceBytes = 0 }, Watchdog = new WatchdogOptions { Enabled = false } }
    };
    var configPath = Path.Combine(folder, "runner.json");
    await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config, ConfigLoader.JsonOptions));
    var (_, paths) = ConfigLoader.Load(configPath);
    var package = Path.Combine(paths.Pending, name);
    Directory.CreateDirectory(package);
    foreach (var source in Directory.EnumerateFiles(AppContext.BaseDirectory))
    {
        var target = Path.Combine(package, Path.GetFileName(source));
        File.Copy(source, target);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(target, File.GetUnixFileMode(source));
    }
    var job = new JobDefinition { Id = name, Executable = Path.GetFileName(Environment.ProcessPath!), Arguments = ["--target"],
        TimeoutSeconds = 15, RequireInput = mode == "control-error",
        Plan = mode == "plan-error" ? [new JobStep { Type = "button", Button = "A", DurationMs = 10 }] :
            mode == "clean" ? [new JobStep { Type = "wait", DelayMs = 100 }, new JobStep { Type = "quit" }] :
            [new JobStep { Type = "wait", DelayMs = 10000 }] };
    await File.WriteAllTextAsync(Path.Combine(package, "job.json"), JsonSerializer.Serialize(job, ConfigLoader.JsonOptions));
    var engine = new RunnerEngine(config, paths);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
    var running = engine.RunAsync(true, deadline.Token);
    if (mode == "cancel")
    {
        while (!engine.State.HasActiveJob && !running.IsCompleted) await Task.Delay(25, deadline.Token);
        await Task.Delay(200, deadline.Token);
        deadline.Cancel();
    }
    await running;
    var directory = Directory.EnumerateDirectories(paths.Results).Single();
    using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "result.json")));
    return (document.RootElement.Clone(), directory, captures);
}
