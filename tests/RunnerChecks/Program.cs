using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;

var failures = 0;
async Task Check(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failures++; Console.Error.WriteLine($"FAIL {name}: {e}"); }
}
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
var root = Path.Combine(Path.GetTempPath(), "xemu-runner-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await Check("preflight validates exact executable hash", async () =>
    {
        var package = Path.Combine(root, "package"); Directory.CreateDirectory(package);
        var exe = OperatingSystem.IsWindows() ? "xemu.exe" : "xemu";
        await File.WriteAllTextAsync(Path.Combine(package, exe), "fixture-not-launched");
        var job = new JobDefinition { Id = "fixture", Executable = exe, ExpectedExecutableSha256 = new string('0', 64) };
        var report = await Preflight.CheckAsync(job, package, root, new PreflightOptions { MinimumFreeSpaceBytes = 0 }, CancellationToken.None);
        Assert(!report.Passed, "Hash mismatch must block launch.");
        job.ExpectedExecutableSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("fixture-not-launched")));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(package, exe), UnixFileMode.UserRead | UnixFileMode.UserExecute);
        report = await Preflight.CheckAsync(job, package, root, new PreflightOptions { MinimumFreeSpaceBytes = 0 }, CancellationToken.None);
        Assert(report.Passed, JsonSerializer.Serialize(report));
        job.TargetOs = OperatingSystem.IsWindows() ? "linux" : "windows";
        report = await Preflight.CheckAsync(job, package, root, new PreflightOptions { MinimumFreeSpaceBytes = 0 }, CancellationToken.None);
        Assert(!report.Passed, "Wrong platform must block launch.");
    });
    await Check("workspace lease excludes a second owner and can be reacquired", () =>
    {
        var workspace = Path.Combine(root, "lease");
        using (var first = WorkspaceLease.Acquire(workspace))
        {
            var rejected = false;
            try { using var second = WorkspaceLease.Acquire(workspace); } catch (IOException) { rejected = true; }
            Assert(rejected, "Second runner acquired the lease.");
        }
        using var again = WorkspaceLease.Acquire(workspace);
        return Task.CompletedTask;
    });
    await Check("ambiguous attempt is held, finalized attempt is archived", () =>
    {
        Assert(AttemptJournal.RecoveryDecision(null, false, 2) == RecoveryAction.Hold, "Missing identity must not be retried.");
        var a = new AttemptRecord { Attempt = 1, Phase = "starting" };
        Assert(AttemptJournal.RecoveryDecision(a, false, 2) == RecoveryAction.Hold, "Launch/PID-write gap must be held.");
        Assert(AttemptJournal.RecoveryDecision(a, true, 2) == RecoveryAction.Archive, "Durable result must not be replayed.");
        a.Phase = "exited"; a.Attempt = 3;
        Assert(AttemptJournal.RecoveryDecision(a, false, 2) == RecoveryAction.Exhausted, "Retries must be bounded.");
        using var current = Process.GetCurrentProcess();
        a.Phase = "running"; a.Attempt = 1; a.ProcessId = current.Id; a.ProcessStartedUtc = current.StartTime.ToUniversalTime();
        Assert(AttemptJournal.RecoveryDecision(a, false, 2) == RecoveryAction.Hold, "Live orphan must be held, never duplicated.");
        return Task.CompletedTask;
    });
    await Check("recovery retains durable retry intent and excludes cleanup failure", () =>
    {
        var results = Path.Combine(root, "retry-intent");
        var attempt = new AttemptRecord { RunId = "run-retry", Attempt = 1, Phase = "exited" };
        var result = Path.Combine(results, attempt.RunId, "result.json");
        AtomicJson.Write(result, new { runId = attempt.RunId, status = "interrupted", retryScheduled = true });
        Assert(!AttemptJournal.HasFinalResult(results, attempt), "Scheduled retry was mistaken for completed archival.");
        AtomicJson.Write(result, new { runId = attempt.RunId, status = "cleanup_failed" });
        Assert(!AttemptJournal.HasFinalResult(results, attempt), "Failed cleanup was mistaken for completed archival.");
        AtomicJson.Write(result, new { runId = attempt.RunId, status = "interrupted", retryScheduled = false });
        Assert(AttemptJournal.HasFinalResult(results, attempt), "Exhausted finalized interruption was not archivable.");
        return Task.CompletedTask;
    });
    await Check("watchdog trips only after consecutive failed probes", async () =>
    {
        int calls = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var watchdog = new ResponsivenessWatchdog(new WatchdogOptions { StartupGraceMs = 0, IntervalMs = 1, RequestTimeoutMs = 100, FailureThreshold = 3 });
        var result = await watchdog.RunAsync(_ => ++calls == 2 ? Task.CompletedTask : Task.FromException(new IOException("fixture")), cts.Token);
        Assert(calls == 5 && result.Failures == 3, $"Unexpected failure sequence: {calls}");
    });
    await Check("preview is single-flight and run-scoped", async () =>
    {
        var cache = new PreviewCache(); int captures = 0;
        async Task<byte[]> Capture(CancellationToken ct) { Interlocked.Increment(ref captures); await Task.Delay(10, ct); return [1, 2, 3]; }
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => cache.GetAsync("run-a", 1000, Capture, CancellationToken.None)));
        Assert(captures == 1, $"Concurrent viewers caused {captures} captures.");
        await cache.GetAsync("run-b", 1000, Capture, CancellationToken.None);
        Assert(captures == 2, "New run reused old screenshot.");
    });
    await Check("preview failure is backed off instead of repeatedly capturing", async () =>
    {
        var cache = new PreviewCache(); int calls = 0;
        for (int i = 0; i < 5; i++)
            try { await cache.GetAsync("failure", 1000, _ => { calls++; throw new IOException("missing QMP"); }, CancellationToken.None); } catch (IOException) { }
        Assert(calls == 1, "Failed preview capture was not rate limited.");
    });
    await Check("large evidence tail is bounded and ranges remain 64 bit", async () =>
    {
        var runs = Path.Combine(root, "results"); var run = Path.Combine(runs, "run-1"); Directory.CreateDirectory(run);
        await File.WriteAllTextAsync(Path.Combine(run, "job.json"), "{}");
        var file = Path.Combine(run, "stdout.log");
        await using (var f = File.Create(file)) { f.SetLength(args.Contains("--large-file") ? 11L * 1024 * 1024 * 1024 : 8L * 1024 * 1024); f.Seek(-4, SeekOrigin.End); await f.WriteAsync("END!"u8.ToArray()); }
        var catalog = new EvidenceCatalog(runs);
        var tail = await catalog.TailAsync("run-1", "stdout.log", 32, CancellationToken.None);
        Assert(tail.Text.EndsWith("END!") && tail.Bytes <= 32, "Tail read was not bounded.");
        var range = FileRange.Parse("bytes=10737418240-10737418243", 11L * 1024 * 1024 * 1024);
        Assert(range.Start == 10737418240L && range.Length == 4, "Range truncated at 32 bits.");
        bool rejected = false; try { catalog.Resolve("../package", "xemu"); } catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "Traversal escaped the results root.");
    });
    await Check("watchdog cancellation is not an unresponsive verdict", async () =>
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        bool cancelled = false;
        try { await new ResponsivenessWatchdog(new WatchdogOptions()).RunAsync(_ => Task.CompletedTask, cts.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Assert(cancelled, "Cancelled watchdog returned a trip instead.");
    });
    await Check("Win32 INPUT layout matches pointer width", () =>
    {
        var type = typeof(XemuTestRunner.Control.WindowsKeyboardInputProvider).GetNestedType("Input", System.Reflection.BindingFlags.NonPublic)!;
        Assert(System.Runtime.InteropServices.Marshal.SizeOf(type) == (IntPtr.Size == 8 ? 40 : 28), "INPUT union layout is wrong.");
        return Task.CompletedTask;
    });
    await Check("transfer started while idle marks the next active run", () =>
    {
        var directory = Path.Combine(root, "transfer"); Directory.CreateDirectory(directory);
        var hub = new ActivityHub();
        using var transfer = hub.TrackTransfer(null);
        using var activity = new RunActivity(directory); hub.Attach("next-run", activity);
        Assert(activity.Snapshot().Intervened, "In-flight transfer was not carried into the new run.");
        hub.Detach(); return Task.CompletedTask;
    });
    await Check("benchmark intervention remains visible in final summary", () =>
    {
        var run = Path.Combine(root, "activity"); Directory.CreateDirectory(run);
        using var activity = new RunActivity(run);
        activity.Mark("manual_input", new { button = "A" }); activity.Mark("preview_capture", null);
        var summary = activity.Snapshot();
        Assert(summary.Intervened && summary.PreviewCaptures == 1 && summary.ManualInputs == 1, "Intervention was lost.");
        return Task.CompletedTask;
    });
}
finally { Directory.Delete(root, recursive: true); }
Console.WriteLine($"Failures: {failures}");
return failures == 0 ? 0 : 1;
