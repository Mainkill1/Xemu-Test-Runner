using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XemuTestRunner.Commands;
using XemuTestRunner.Config;
using XemuTestRunner.Control;
using XemuTestRunner.Diagnostics;
using XemuTestRunner.Monitoring.Providers;
using XemuTestRunner.Networking;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;
using XemuTestRunner.Runtime;
using XemuTestRunner.Workstation;

if (args.Contains("--fixture-process", StringComparer.Ordinal))
{
    // This fixture deliberately has no QMP endpoint; extra arguments fail.
    if (args.Length != 2 || args[0] != "--fixture-process") return 73;
    if (args[1] == "timeout") await Task.Delay(Timeout.Infinite);
    await Task.Delay(200);
    Console.WriteLine("{\"passed\":true,\"fixtureValue\":1234}");
    return args[1] == "failure" ? 17 : 0;
}
if (args.Contains("--fake-xemu", StringComparer.Ordinal))
    return await FakeXemuHost.RunAsync(args);
if (args.Contains("--fake-diagnostic-delay", StringComparer.Ordinal))
{
    await Task.Delay(2000);
    return 0;
}
if (args.Length == 2 && args[0] == "--fake-screenshot")
{
    FakeXemuHost.WritePng(args[1]);
    return 0;
}
if (args.Length == 2 && args[0] == "--fake-screenshot-writer")
{
    await Task.Delay(300);
    FakeXemuHost.WritePng(args[1]);
    return 0;
}
if (args.Length == 2 && args[0] == "--fake-screenshot-delayed")
{
    var writer = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    writer.ArgumentList.Add("--fake-screenshot-writer");
    writer.ArgumentList.Add(args[1]);
    using var process = Process.Start(writer)
        ?? throw new IOException("Failed to start delayed screenshot fixture.");
    return 0;
}
if (args.Length == 2 && args[0] == "--fake-screenshot-complete-hang")
{
    FakeXemuHost.WritePng(args[1]);
    await Task.Delay(TimeSpan.FromSeconds(30));
    return 0;
}

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
    await Check("standalone definitions reject unsupported control", () =>
    {
        StandaloneProcessChecks.ValidateDefinition(root);
        return Task.CompletedTask;
    });
    await Check("standalone process rejects ad hoc diagnostic attachment", () =>
    {
        StandaloneProcessChecks.RejectDiagnosticAttachment(root);
        return Task.CompletedTask;
    });
    foreach (var mode in new[] { "success", "failure", "timeout" })
        await Check("standalone process " + mode,
            () => StandaloneProcessChecks.RunAsync(root, mode));
    await Check("external screenshot accepts complete PNG from hung provider", async () =>
    {
        var directory = Path.Combine(root, "screenshot-complete-hang");
        Directory.CreateDirectory(directory);
        using var control = new XemuControlManager(new XemuControlOptions
        {
            InputProvider = "unavailable",
            ScreenshotProvider = "external",
            ScreenshotExecutable = Environment.ProcessPath!,
            ScreenshotArguments = ["--fake-screenshot-complete-hang", "{path}"],
            ScreenshotTimeoutMs = 200
        });
        using var current = Process.GetCurrentProcess();
        control.Begin(current, directory, 0);

        var path = await control.CaptureScreenshotAsync("complete-before-timeout", CancellationToken.None);

        Assert(File.Exists(path), "Complete PNG was discarded after the provider failed to exit.");
    });

    await Check("QMP readiness retries a timed-out handshake", async () =>
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = ServeReadinessAfterTimeoutAsync(listener, deadline.Token);
        using var process = Process.GetCurrentProcess();
        using var control = new XemuControlManager(new XemuControlOptions
        {
            ConnectTimeoutMs = 4500,
            InputProvider = "unavailable"
        });
        control.Begin(process, root, ((IPEndPoint)listener.LocalEndpoint).Port);
        try
        {
            await control.WaitUntilReadyAsync(deadline.Token);
            var status = await control.QueryStatusAsync(deadline.Token);
            await server;
            Assert(status.GetProperty("status").GetString() == "paused",
                "Ready control cannot query the endpoint after the first handshake timed out.");
        }
        finally
        {
            deadline.Cancel();
            try { await server; } catch (OperationCanceledException) { }
        }
    });

    await Check("QMP readiness stops at the configured deadline", async () =>
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var process = Process.GetCurrentProcess();
        using var control = new XemuControlManager(new XemuControlOptions
        {
            ConnectTimeoutMs = 250,
            InputProvider = "unavailable"
        });
        control.Begin(process, root, ((IPEndPoint)listener.LocalEndpoint).Port);
        var started = Stopwatch.StartNew();
        try
        {
            await control.WaitUntilReadyAsync(CancellationToken.None);
            throw new Exception("Silent QMP endpoint became ready.");
        }
        catch (TimeoutException ex)
        {
            Assert(ex.InnerException is TimeoutException,
                "The attempt timeout escaped instead of exhausting the readiness deadline.");
            Assert(started.Elapsed < TimeSpan.FromSeconds(2),
                "Silent QMP endpoint did not respect the readiness deadline.");
        }
    });

    await Check("QMP readiness preserves caller cancellation", async () =>
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var process = Process.GetCurrentProcess();
        using var control = new XemuControlManager(new XemuControlOptions
        {
            ConnectTimeoutMs = 4500,
            InputProvider = "unavailable"
        });
        control.Begin(process, root, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var cancelled = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var ready = control.WaitUntilReadyAsync(cancelled.Token);
        using var accepted = await listener.AcceptTcpClientAsync(cancelled.Token);
        cancelled.Cancel();
        try
        {
            await ready.WaitAsync(TimeSpan.FromSeconds(2));
            throw new Exception("Cancelled readiness returned success.");
        }
        catch (OperationCanceledException)
        {
            Assert(cancelled.IsCancellationRequested,
                "Readiness was cancelled without a caller cancellation.");
        }
    });

    await Check("HTTP defaults to a remotely reachable bind address", () =>
    {
        var http = new HttpOptions();
        Assert(http.Enabled, "The HTTP control plane is disabled by default.");
        Assert(http.BindAddress == "0.0.0.0", "The HTTP control plane no longer listens on all IPv4 interfaces.");
        Assert(http.Port == 9368, "The documented HTTP port changed unexpectedly.");
        return Task.CompletedTask;
    });

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
    await Check("artifact checks reject blank screenshots when requested", async () =>
    {
        var directory = Path.Combine(root, "image-artifacts");
        Directory.CreateDirectory(directory);
        var black = Path.Combine(directory, "black.png");
        var visible = Path.Combine(directory, "visible.png");
        await File.WriteAllBytesAsync(black,
        [
            0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
            0x00, 0x00, 0x00, 0x0d, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53,
            0xde, 0x00, 0x00, 0x00, 0x0c, 0x49, 0x44, 0x41,
            0x54, 0x78, 0x9c, 0x63, 0x60, 0x60, 0x60, 0x00,
            0x00, 0x00, 0x04, 0x00, 0x01, 0xf6, 0x17, 0x38,
            0x55, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45,
            0x4e, 0x44, 0xae, 0x42, 0x60, 0x82
        ]);
        FakeXemuHost.WritePng(visible);

        var requirement = new ArtifactCheckDefinition
        {
            Scope = "result",
            Path = "screenshots/frame.png",
            MinimumNonBlackPixelRatio = 0.01
        };
        var blackResult = await ArtifactInspector.CheckAsync(requirement, black, CancellationToken.None);
        Assert(!blackResult.Passed && blackResult.Detail.Contains("non-black", StringComparison.OrdinalIgnoreCase),
            "An all-black PNG passed the declared image-content check.");

        var visibleResult = await ArtifactInspector.CheckAsync(requirement, visible, CancellationToken.None);
        Assert(visibleResult.Passed, visibleResult.Detail);

        requirement.MinimumNonBlackPixelRatio = null;
        var compatibilityResult = await ArtifactInspector.CheckAsync(requirement, black, CancellationToken.None);
        Assert(compatibilityResult.Passed,
            "Existing artifact contracts changed behavior without opting into image-content validation.");
    });
    await Check("artifact checks support thresholded image regions", async () =>
    {
        var directory = Path.Combine(root, "image-region-artifacts");
        Directory.CreateDirectory(directory);
        var image = Path.Combine(directory, "regions.png");
        WriteRgbPng(image, 4, 2, (x, _) => x switch
        {
            0 => (255, 255, 255),
            1 or 3 => (100, 100, 100),
            _ => (0, 0, 0)
        });

        var requirement = new ArtifactCheckDefinition
        {
            Scope = "result",
            Path = "screenshots/frame.png",
            MinimumNonBlackPixelRatio = 0.5,
            NonBlackPixelThreshold = 200,
            ImageRegion = new ImageRegionDefinition
            {
                X = 0,
                Y = 0,
                Width = 0.5,
                Height = 1
            }
        };
        var brightLeft = await ArtifactInspector.CheckAsync(
            requirement, image, CancellationToken.None);
        Assert(brightLeft.Passed, brightLeft.Detail);

        requirement.ImageRegion.X = 0.5;
        var darkRight = await ArtifactInspector.CheckAsync(
            requirement, image, CancellationToken.None);
        Assert(!darkRight.Passed &&
               darkRight.Detail.Contains("region", StringComparison.OrdinalIgnoreCase),
            "A region without pixels above the declared threshold passed.");
    });
    await Check("artifact checks distinguish a declared measurement scene", async () =>
    {
        var directory = Path.Combine(root, "image-scene-artifacts");
        Directory.CreateDirectory(directory);
        var expected = Path.Combine(directory, "expected.png");
        var wrong = Path.Combine(directory, "wrong.png");
        WriteRgbPng(expected, 90, 80, (x, y) =>
            x < 45 == y < 40 ? (byte.MaxValue, (byte)20, (byte)10) : ((byte)5, (byte)10, (byte)200));
        WriteRgbPng(wrong, 90, 80, (x, _) =>
            x < 45 ? (byte.MaxValue, byte.MaxValue, byte.MaxValue) : ((byte)0, (byte)0, (byte)0));

        string fingerprint;
        await using (var stream = File.OpenRead(expected))
            fingerprint = await PngInspector.CalculateDifferenceHashAsync(
                stream, null, CancellationToken.None);
        var requirement = new ArtifactCheckDefinition
        {
            Scope = "result",
            Path = "screenshots/recording-start.png",
            ExpectedImageDHash = fingerprint,
            MaximumImageHammingDistance = 0
        };
        var matching = await ArtifactInspector.CheckAsync(
            requirement, expected, CancellationToken.None);
        Assert(matching.Passed, matching.Detail);
        var rejected = await ArtifactInspector.CheckAsync(
            requirement, wrong, CancellationToken.None);
        Assert(!rejected.Passed &&
               rejected.Detail.Contains("fingerprint", StringComparison.OrdinalIgnoreCase),
            "A different scene passed the declared image fingerprint.");
    });
    await Check("live scene wait captures until the declared scene and preserves the match", async () =>
    {
        var directory = Path.Combine(root, "live-scene-match");
        var previewDirectory = Path.Combine(directory, ".preview");
        Directory.CreateDirectory(previewDirectory);
        var wrong = Path.Combine(previewDirectory, "wrong.png");
        var expected = Path.Combine(previewDirectory, "expected.png");
        WriteRgbPng(wrong, 90, 80, (x, _) =>
            x < 45 ? (byte.MaxValue, byte.MaxValue, byte.MaxValue) : ((byte)0, (byte)0, (byte)0));
        WriteRgbPng(expected, 90, 80, (x, y) =>
            x < 45 == y < 40 ? (byte.MaxValue, (byte)20, (byte)10) : ((byte)5, (byte)10, (byte)200));

        string fingerprint;
        await using (var stream = File.OpenRead(expected))
            fingerprint = await PngInspector.CalculateDifferenceHashAsync(
                stream, null, CancellationToken.None);
        var condition = new ArtifactCheckDefinition
        {
            Name = "measurement-ready",
            Scope = "result",
            Path = "screenshots/measurement-ready.png",
            ExpectedImageDHash = fingerprint,
            MaximumImageHammingDistance = 0
        };
        var captures = new Queue<string>([wrong, expected]);
        await LiveSceneConditionWaiter.WaitAsync(
            condition, 1000, 25, directory,
            _ => Task.FromResult(captures.Dequeue()), CancellationToken.None);

        var preserved = Path.Combine(directory, "screenshots", "measurement-ready.png");
        Assert(File.Exists(preserved), "The matching live scene was not preserved as result evidence.");
        var inspection = await ArtifactInspector.CheckAsync(
            condition, preserved, CancellationToken.None);
        Assert(inspection.Passed, inspection.Detail);
        Assert(captures.Count == 0, "The live scene wait did not inspect both candidates.");
    });
    await Check("live scene wait reports the last mismatch on timeout", async () =>
    {
        var directory = Path.Combine(root, "live-scene-timeout");
        var previewDirectory = Path.Combine(directory, ".preview");
        Directory.CreateDirectory(previewDirectory);
        var wrong = Path.Combine(previewDirectory, "wrong.png");
        WriteRgbPng(wrong, 90, 80, (x, _) =>
            x < 45 ? (byte.MaxValue, byte.MaxValue, byte.MaxValue) : ((byte)0, (byte)0, (byte)0));
        var condition = new ArtifactCheckDefinition
        {
            Name = "measurement-ready",
            Scope = "result",
            Path = "screenshots/measurement-ready.png",
            ExpectedImageDHash = "0123456789abcdef",
            MaximumImageHammingDistance = 0
        };

        try
        {
            await LiveSceneConditionWaiter.WaitAsync(
                condition, 80, 25, directory,
                _ =>
                {
                    WriteRgbPng(wrong, 90, 80, (x, _) =>
                        x < 45 ? (byte.MaxValue, byte.MaxValue, byte.MaxValue) : ((byte)0, (byte)0, (byte)0));
                    return Task.FromResult(wrong);
                }, CancellationToken.None);
            throw new Exception("A live scene that never matched returned success.");
        }
        catch (TimeoutException error)
        {
            Assert(error.Message.Contains("fingerprint distance", StringComparison.OrdinalIgnoreCase),
                "The live scene timeout omitted the last mismatch detail.");
        }
    });
    await Check("input benchmarks require a measurement-start scene checkpoint", async () =>
    {
        var package = Path.Combine(root, "benchmark-scene-contract");
        Directory.CreateDirectory(package);
        await File.WriteAllTextAsync(Path.Combine(package, "xemu"), "fixture");
        var job = new JobDefinition
        {
            Id = "benchmark-scene-contract",
            Executable = "xemu",
            RequireInput = true,
            Operations = new OperationPolicyDefinition { Mode = "benchmark" },
            Plan =
            [
                new JobStep { Type = "segment_start", Name = "stationary-start" },
                new JobStep { Type = "segment_end", Name = "stationary-start" }
            ]
        };
        AtomicJson.Write(Path.Combine(package, "job.json"), job);
        try
        {
            _ = JobDefinition.LoadPackage(package);
            throw new Exception("An input benchmark without a scene checkpoint was accepted.");
        }
        catch (InvalidDataException error)
        {
            Assert(error.Message.Contains("wait_for_scene", StringComparison.OrdinalIgnoreCase),
                "The missing scene checkpoint produced an unrelated validation error.");
        }

        job.Workload.CorrectnessChecks.Add(new ArtifactCheckDefinition
        {
            Name = "measurement-start-scene",
            Scope = "result",
            Path = "screenshots/recording-start.png",
            ExpectedImageDHash = "0123456789abcdef",
            MaximumImageHammingDistance = 8
        });
        AtomicJson.Write(Path.Combine(package, "job.json"), job);
        try
        {
            _ = JobDefinition.LoadPackage(package);
            throw new Exception("A post-run scene check without a live wait was accepted.");
        }
        catch (InvalidDataException error)
        {
            Assert(error.Message.Contains("wait_for_scene", StringComparison.OrdinalIgnoreCase),
                "The missing live scene wait produced an unrelated validation error.");
        }

        job.Plan.Insert(0, new JobStep
        {
            Type = "wait_for_scene",
            TimeoutMs = 60000,
            PollIntervalMs = 250,
            Condition = new ArtifactCheckDefinition
            {
                Name = "measurement-start-scene",
                Scope = "result",
                Path = "screenshots/recording-start.png",
                ExpectedImageDHash = "0123456789abcdef",
                MaximumImageHammingDistance = 64
            }
        });
        AtomicJson.Write(Path.Combine(package, "job.json"), job);
        try
        {
            _ = JobDefinition.LoadPackage(package);
            throw new Exception("A live scene wait that accepts every hash was accepted.");
        }
        catch (InvalidDataException error)
        {
            Assert(error.Message.Contains("no greater than 16", StringComparison.OrdinalIgnoreCase),
                "The lax live scene checkpoint produced an unrelated validation error.");
        }

        job.Plan[0].Condition!.MaximumImageHammingDistance = 8;
        AtomicJson.Write(Path.Combine(package, "job.json"), job);
        _ = JobDefinition.LoadPackage(package);
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
    await Check("recovery requires released ownership before archiving durable results", () =>
    {
        Assert(AttemptJournal.RecoveryDecision(null, false, 2) == RecoveryAction.Hold,
            "Missing identity must not be retried.");

        var attempt = new AttemptRecord { Attempt = 1, Phase = "starting" };
        Assert(AttemptJournal.RecoveryDecision(attempt, false, 2) == RecoveryAction.Hold,
            "Launch/PID-write gap must be held.");
        Assert(AttemptJournal.RecoveryDecision(attempt, true, 2) == RecoveryAction.Hold,
            "A result file cannot release ambiguous launch ownership.");

        attempt.Phase = "finalized";
        Assert(AttemptJournal.RecoveryDecision(attempt, true, 2) == RecoveryAction.Archive,
            "A finalized attempt with released ownership must archive without replay.");
        attempt.Phase = "held";
        Assert(AttemptJournal.RecoveryDecision(attempt, true, 2) == RecoveryAction.Hold,
            "A durable result must not clear an explicit hold.");

        attempt.Phase = "exited";
        attempt.Attempt = 3;
        Assert(AttemptJournal.RecoveryDecision(attempt, false, 2) == RecoveryAction.Exhausted,
            "Retries must be bounded.");

        using var current = Process.GetCurrentProcess();
        attempt.Phase = "running";
        attempt.Attempt = 1;
        attempt.ProcessId = current.Id;
        attempt.ProcessStartedUtc = current.StartTime.ToUniversalTime();
        Assert(AttemptJournal.RecoveryDecision(attempt, false, 2) == RecoveryAction.Hold,
            "A live orphan must be held, never duplicated.");
        Assert(AttemptJournal.RecoveryDecision(attempt, true, 2) == RecoveryAction.Hold,
            "A durable result cannot release a matching live process.");
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
    await Check("package archival retries transient file locks", async () =>
    {
        var fixture = Path.Combine(root, "archive-retry");
        var paths = new RunnerPaths(
            Path.Combine(fixture, "runner.json"),
            fixture,
            Path.Combine(fixture, "Pending"),
            Path.Combine(fixture, "Testing"),
            Path.Combine(fixture, "Tested"),
            Path.Combine(fixture, "Results"),
            Path.Combine(fixture, "Files"));
        var attempts = 0;
        var queue = new JobQueue(
            new RunnerConfig(),
            paths,
            (source, destination) =>
            {
                attempts++;
                if (attempts < 3)
                    throw new IOException("fixture lock");
                Directory.Move(source, destination);
            });
        queue.EnsureDirectories();
        var package = Path.Combine(paths.Testing, "completed-package");
        Directory.CreateDirectory(package);

        var archived = await queue.CompleteAsync(
            package,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert(attempts == 3, $"Archive move used {attempts} attempts instead of retrying twice.");
        Assert(Directory.Exists(archived) && !Directory.Exists(package),
            "Finalized package was not archived after its transient lock cleared.");

        var held = Path.Combine(paths.Testing, "persistently-locked");
        Directory.CreateDirectory(held);
        var blockedQueue = new JobQueue(
            new RunnerConfig(),
            paths,
            (_, _) => throw new IOException("persistent fixture lock"));
        var reported = false;
        try
        {
            await blockedQueue.CompleteAsync(
                held,
                TimeSpan.FromMilliseconds(10),
                CancellationToken.None);
        }
        catch (IOException exception)
        {
            reported = exception.Message.Contains("left in Testing for recovery", StringComparison.Ordinal);
        }
        Assert(reported && Directory.Exists(held),
            "Persistent archive lock did not leave a durable recoverable package in Testing.");
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

    await Check("diagnostic recipes reject ambiguous RenderDoc capture semantics", () =>
    {
        var genericFive = new DiagnosticRecipe
        {
            Id = "rdoc-generic",
            Type = "renderdoc",
            RenderDocTrigger = "target-control",
            Frames = 5
        };
        var rejected = false;
        try { genericFive.Validate(); } catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "Generic RenderDoc trigger accepted a multi-frame request whose completion shape is ambiguous.");

        var tracedGuest = new DiagnosticRecipe
        {
            Id = "rdoc-guest",
            Type = "renderdoc",
            RenderDocTrigger = "xemu-hotkey",
            Frames = 5,
            TracePgraph = true
        };
        tracedGuest.Validate();

        var memory = new DiagnosticRecipe { Id = "memory", Type = "memory_dump", Address = 0x1000, Size = 4096 };
        memory.Validate();
        return Task.CompletedTask;
    });

    await Check("perf clock selection is explicit and opt-in", () =>
    {
        var defaultRecipe = new DiagnosticRecipe
        {
            Id = "perf-default",
            Type = "perf",
            Frequency = 997,
            CallGraph = "fp"
        };
        var defaultArgs = DiagnosticHub.BuildPerfRecordArguments(42, defaultRecipe, "/tmp/default.data");
        Assert(!defaultArgs.Contains("-k", StringComparer.Ordinal),
            "Default perf recipe unexpectedly selected a clock ID.");

        var jitRecipe = new DiagnosticRecipe
        {
            Id = "perf-jit",
            Type = "perf",
            Frequency = 997,
            CallGraph = "fp",
            ClockId = 1
        };
        jitRecipe.Validate();
        var jitArgs = DiagnosticHub.BuildPerfRecordArguments(42, jitRecipe, "/tmp/jit.data");
        var clockOption = jitArgs.IndexOf("-k");
        Assert(clockOption >= 0 && clockOption + 1 < jitArgs.Count && jitArgs[clockOption + 1] == "1",
            "ClockId 1 was not emitted as '-k 1'.");

        jitRecipe.ClockId = -1;
        var rejected = false;
        try { jitRecipe.Validate(); } catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "A negative perf ClockId was accepted.");
        return Task.CompletedTask;
    });

    await Check("job package validates diagnostic references and ignores runtime package property", async () =>
    {
        var package = Path.Combine(root, "diagnostic-package");
        Directory.CreateDirectory(package);
        var executable = OperatingSystem.IsWindows() ? "xemu.exe" : "xemu";
        await File.WriteAllTextAsync(Path.Combine(package, executable), "fixture");

        var jobJson = JsonSerializer.Serialize(new
        {
            Id = "diagnostic-fixture",
            Executable = executable,
            LaunchMode = "direct",
            StartPaused = true,
            RequireInput = true,
            Diagnostics = new[]
            {
                new { Id = "monitor-state", Type = "monitor", MonitorCommand = "info registers" }
            },
            Plan = new[]
            {
                new { Type = "diagnostic", DiagnosticId = "monitor-state" }
            }
        }, ConfigLoader.JsonOptions);
        await File.WriteAllTextAsync(Path.Combine(package, "job.json"), jobJson);

        var loaded = JobDefinition.LoadPackage(package);
        Assert(loaded.Diagnostics.Count == 1 && loaded.Plan[0].DiagnosticId == "monitor-state",
            "Diagnostic recipe/plan reference was not retained.");
        Assert(Path.GetFullPath(package) == loaded.PackageDirectory,
            "Runtime package identity was not attached.");

        var serialized = JsonSerializer.Serialize(loaded, ConfigLoader.JsonOptions);
        Assert(!serialized.Contains("PackageDirectory", StringComparison.Ordinal),
            "Runtime-only PackageDirectory leaked into job JSON.");

        loaded.Plan[0].DiagnosticId = "missing";
        await File.WriteAllTextAsync(Path.Combine(package, "job.json"),
            JsonSerializer.Serialize(loaded, ConfigLoader.JsonOptions));
        var rejected = false;
        try { _ = JobDefinition.LoadPackage(package); } catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "Unknown diagnostic plan reference was accepted.");
    });

    await Check("quit is accepted only as the final plan step", () =>
    {
        var package = Path.Combine(root, "quit-order");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "xemu"), "fixture");
        AtomicJson.Write(Path.Combine(package, "job.json"), new JobDefinition
        {
            Id = "quit-order",
            Executable = "xemu",
            Plan =
            [
                new JobStep { Type = "quit" },
                new JobStep { Type = "wait", DelayMs = 1 }
            ]
        });
        var rejected = false;
        try { _ = JobDefinition.LoadPackage(package); }
        catch (InvalidDataException ex) when (ex.Message.Contains("final plan step", StringComparison.Ordinal))
        {
            rejected = true;
        }
        Assert(rejected, "A plan continued after terminating its target.");
        return Task.CompletedTask;
    });

    await Check("diagnostic tool catalog reports an explicit missing executable", () =>
    {
        var options = new DiagnosticsOptions { Addr2LineExecutable = Path.Combine(root, "definitely-not-addr2line") };
        var capability = new DiagnosticToolCatalog(options).Get("addr2line");
        Assert(!capability.Available && capability.ResolvedPath is null,
            "Missing configured diagnostic tool was reported available.");
        return Task.CompletedTask;
    });

    await Check("QMP monitor and external recipes validate bounded configuration", () =>
    {
        new DiagnosticRecipe { Id = "monitor", Type = "monitor", MonitorCommand = "info mtree" }.Validate();
        new DiagnosticRecipe
        {
            Id = "qmp",
            Type = "qmp",
            QmpCommand = "query-status",
            QmpArguments = new Dictionary<string, JsonElement>()
        }.Validate();
        new DiagnosticRecipe
        {
            Id = "external",
            Type = "external",
            ToolExecutable = "tool",
            ToolArguments = ["--pid", "{pid}"]
        }.Validate();

        var invalid = new DiagnosticRecipe { Id = "monitor-bad", Type = "monitor", MonitorCommand = new string('x', 513) };
        var rejected = false;
        try { invalid.Validate(); } catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "Unbounded monitor command was accepted.");
        return Task.CompletedTask;
    });

    await Check("benchmark intervention remains visible in final summary", () =>
    {
        var run = Path.Combine(root, "activity"); Directory.CreateDirectory(run);
        using var activity = new RunActivity(run);
        activity.Mark("manual_input", new { button = "A" });
        activity.Mark("preview_capture", null);
        activity.Mark("diagnostic", new { id = "perf-window" });
        var summary = activity.Snapshot();
        Assert(summary.Intervened && summary.PreviewCaptures == 1 && summary.ManualInputs == 1 &&
            summary.Diagnostics == 1, "Intervention was lost.");
        return Task.CompletedTask;
    });

    await Check("native input failure cannot qualify a comparison", () =>
    {
        var job = new JobDefinition { Experiment = new ExperimentDefinition
        {
            Id = "controller-comparison", Variant = "candidate"
        } };
        var workload = new WorkloadEvaluation(CorrectnessOutcome.Passed,
            EvidenceOutcome.Complete, [], []);
        var activity = new ActivitySummary(false, 0, 0, 0, 0, 0, 0, 0);
        var assessment = RunAssessmentEvaluator.Evaluate("input_failure", workload, activity, job);
        Assert(assessment.Execution == ExecutionOutcome.Failed &&
            assessment.Comparison == ComparisonEligibility.Ineligible,
            "Failed native input was assessed as an eligible or unstarted comparison.");
        return Task.CompletedTask;
    });

    await Check("workstation rendering risk flags lock display sleep and battery saver", () =>
    {
        Assert(new WorkstationStateSnapshot { SessionLocked = true }.RenderingRisk,
            "Locked workstation was not marked as a rendering risk.");
        Assert(new WorkstationStateSnapshot { DisplayState = "off" }.RenderingRisk,
            "Display-off workstation was not marked as a rendering risk.");
        Assert(new WorkstationStateSnapshot { PowerState = "suspending" }.RenderingRisk,
            "Suspending workstation was not marked as a rendering risk.");
        Assert(new WorkstationStateSnapshot { BatterySaver = true }.RenderingRisk,
            "Battery saver was not marked as a rendering risk.");
        Assert(!new WorkstationStateSnapshot
        {
            SessionLocked = false,
            DisplayState = "on",
            PowerState = "awake",
            BatterySaver = false
        }.RenderingRisk, "Normal interactive workstation was marked risky.");
        return Task.CompletedTask;
    });

    await Check("runner publishes host telemetry while idle", async () =>
    {
        var fixture = Path.Combine(root, "idle-telemetry");
        var configPath = Path.Combine(fixture, "runner.json");
        Directory.CreateDirectory(fixture);

        var config = new RunnerConfig
        {
            Workspace = "workspace",
            Http = new HttpOptions { Enabled = false },
            Monitoring = new MonitoringOptions
            {
                Enabled = true,
                IntervalMs = 50,
                FlushIntervalMs = 100,
                BufferCapacity = 64,
                Gpu = new GpuOptions { Enabled = false }
            },
            XemuControl = new XemuControlOptions { Enabled = false },
            Reliability = new ReliabilityOptions
            {
                Preflight = new PreflightOptions { MinimumFreeSpaceBytes = 0 },
                Watchdog = new WatchdogOptions { Enabled = false }
            }
        };

        await File.WriteAllTextAsync(
            configPath,
            JsonSerializer.Serialize(config, ConfigLoader.JsonOptions));

        var (_, paths) = ConfigLoader.Load(configPath);
        var engine = new XemuTestRunner.Runtime.RunnerEngine(config, paths);
        using var stop = new CancellationTokenSource();
        var runTask = engine.RunAsync(once: false, stop.Token);

        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!deadline.IsCancellationRequested)
            {
                var snapshot = engine.State.Snapshot();
                if (snapshot.CurrentJob is null &&
                    snapshot.LatestMetric?.HostMemoryTotalBytes is > 0 &&
                    snapshot.LatestMetric.HostCpuPercent is >= 0)
                    break;

                await Task.Delay(50, deadline.Token);
            }

            var idle = engine.State.Snapshot();
            Assert(idle.CurrentJob is null, "Idle telemetry unexpectedly attached a target process.");
            Assert(idle.LatestMetric?.HostMemoryTotalBytes is > 0,
                "Idle runner did not publish host memory.");
            Assert(idle.LatestMetric?.HostCpuPercent is >= 0,
                "Idle runner did not publish host CPU after priming.");
        }
        finally
        {
            stop.Cancel();
            await runTask;
        }

        var idleCsv = Directory.Exists(paths.Workspace)
            ? Directory.EnumerateFiles(
                paths.Workspace,
                "*.csv",
                SearchOption.AllDirectories).ToArray()
            : [];
        Assert(
            idleCsv.Length == 0,
            "Idle telemetry wrote CSV data before a test started: " +
            string.Join(", ", idleCsv));
    });

    await Check("queue holds incomplete and changing packages before claim", async () =>
    {
        var fixture = Path.Combine(root, "queue-stability");
        var configPath = Path.Combine(fixture, "runner.json");
        Directory.CreateDirectory(fixture);

        var config = new RunnerConfig
        {
            Workspace = "workspace",
            Queue = new QueueOptions
            {
                PackageStabilityMs = 100,
                ScanIntervalMs = 25
            },
            Http = new HttpOptions { Enabled = false },
            Monitoring = new MonitoringOptions { Enabled = false },
            XemuControl = new XemuControlOptions { Enabled = false },
            Reliability = new ReliabilityOptions
            {
                Preflight = new PreflightOptions
                {
                    MinimumFreeSpaceBytes = 0
                },
                Watchdog = new WatchdogOptions { Enabled = false }
            }
        };

        await File.WriteAllTextAsync(
            configPath,
            JsonSerializer.Serialize(config, ConfigLoader.JsonOptions));

        var (_, paths) = ConfigLoader.Load(configPath);
        var queue = new JobQueue(config, paths);
        queue.EnsureDirectories();

        var incomplete = Path.Combine(paths.Pending, "incomplete");
        Directory.CreateDirectory(incomplete);

        var first = queue.TryClaimNext();
        Assert(
            first.Package is null &&
            first.Issue?.Code == "package_incomplete",
            "Visible package without job.json did not report package_incomplete.");

        Directory.Delete(incomplete, recursive: true);

        var package = Path.Combine(paths.Pending, "changing");
        Directory.CreateDirectory(package);
        var executable = Path.Combine(package, "xemu");
        await File.WriteAllTextAsync(executable, "first");
        await File.WriteAllTextAsync(
            Path.Combine(package, "job.json"),
            JsonSerializer.Serialize(
                new JobDefinition
                {
                    Id = "changing",
                    Executable = "xemu"
                },
                ConfigLoader.JsonOptions));

        var stabilizing = queue.TryClaimNext();
        Assert(
            stabilizing.Package is null &&
            stabilizing.Issue?.Code == "package_stabilizing",
            "New package was claimed without a stability observation.");

        await File.AppendAllTextAsync(executable, "-changed");
        await Task.Delay(110);

        var changed = queue.TryClaimNext();
        Assert(
            changed.Package is null &&
            changed.Issue?.Code == "package_stabilizing",
            "Changing executable did not reset package stability.");

        await Task.Delay(110);
        var claimed = queue.TryClaimNext();
        Assert(
            claimed.Package is not null &&
            claimed.Issue is null,
            "Stable package was not claimed after the stability window.");

        await File.AppendAllTextAsync(
            Path.Combine(claimed.Package!, "xemu"),
            "-post-claim");

        var integrity = queue.VerifyClaimedPackage(claimed.Package!);
        Assert(
            integrity?.Code == "package_changed_during_preflight",
            "Post-claim package mutation was not detected before launch.");
    });

    await Check("automation exit codes distinguish success failure and queue blockage", () =>
    {
        var success = new XemuTestRunner.Runtime.RunnerState();
        success.EndJob("completed", "ok-job", "/tmp/ok");
        Assert(
            RunCommand.DetermineExitCode(success.Snapshot(), cancelled: false) == 0,
            "Completed automation run did not return exit code 0.");

        var failed = new XemuTestRunner.Runtime.RunnerState();
        failed.EndJob("timeout", "failed-job", "/tmp/failed");
        Assert(
            RunCommand.DetermineExitCode(failed.Snapshot(), cancelled: false) == 2,
            "Failed test did not return automation exit code 2.");

        var blocked = new XemuTestRunner.Runtime.RunnerState();
        blocked.SetQueueIssue(new QueueIssue(
            "package_busy",
            "build",
            "busy",
            DateTimeOffset.UtcNow,
            Retryable: true,
            HoldsTesting: false));
        Assert(
            RunCommand.DetermineExitCode(blocked.Snapshot(), cancelled: false) == 3,
            "Queue issue did not return automation exit code 3.");

        Assert(
            RunCommand.DetermineExitCode(success.Snapshot(), cancelled: true) == 130,
            "Cancellation did not return exit code 130.");
        return Task.CompletedTask;
    });

    await Check("terminal display loss leaves the engine authoritative", async () =>
    {
        var engineCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var guardedRun = RunCommand.WaitForEngineAfterTerminalFailureAsync(
            () => Task.FromException(new IOException("console handle lost")),
            engineCompletion.Task);

        await Task.Delay(50);
        Assert(
            !guardedRun.IsCompleted,
            "Recoverable display failure stopped waiting for the engine.");

        engineCompletion.SetResult();
        await guardedRun.WaitAsync(TimeSpan.FromSeconds(2));
    });

    await Check("engine failure survives terminal display loss", async () =>
    {
        var expected = new ApplicationException("engine failed");
        var observed = false;

        try
        {
            await RunCommand.WaitForEngineAfterTerminalFailureAsync(
                () => Task.FromException(new IOException("console handle lost")),
                Task.FromException(expected));
        }
        catch (ApplicationException ex) when (ReferenceEquals(ex, expected))
        {
            observed = true;
        }

        Assert(observed, "Engine failure was swallowed after display loss.");
    });

    await Check("terminal error reporting preserves the original failure", () =>
    {
        var expected = new ApplicationException("engine failed");
        Exception? fallbackError = null;

        RunCommand.ReportInteractiveFailure(
            expected,
            _ => throw new IOException("console handle lost"),
            error => fallbackError = error);

        Assert(
            ReferenceEquals(fallbackError, expected),
            "Fallback reporting did not receive the original engine failure.");
        return Task.CompletedTask;
    });

    await Check("missing terminal fallback cannot terminate the runner", () =>
    {
        RunCommand.ReportInteractiveFailure(
            new ApplicationException("engine failed"),
            _ => throw new IOException("console handle lost"),
            _ => throw new IOException("stderr handle lost"));
        return Task.CompletedTask;
    });

    foreach (var standaloneNext in new[] { false, true })
    await Check(standaloneNext
        ? "standalone process does not inherit previous xemu diagnostics"
        : "diagnostic timeout archives failure and continues the queue", async () =>
    {
        var fixture = Path.Combine(root, standaloneNext ? "diagnostic-process-next" : "diagnostic-timeout");
        Directory.CreateDirectory(fixture);
        var configPath = Path.Combine(fixture, "runner.json");
        var config = new RunnerConfig
        {
            Workspace = "workspace",
            Queue = new QueueOptions { PackageStabilityMs = 100, ScanIntervalMs = 25 },
            Http = new HttpOptions { Enabled = false },
            Monitoring = new MonitoringOptions { Enabled = false },
            XemuControl = new XemuControlOptions
            {
                Enabled = true, ConnectTimeoutMs = 3000, InputProvider = "unavailable"
            },
            Diagnostics = new DiagnosticsOptions
            {
                ToolTimeoutMs = 100,
                AutoHangBundle = false,
                AutoFailureBundle = false,
                CrashReports = new CrashCaptureOptions { Enabled = false }
            },
            Reliability = new ReliabilityOptions
            {
                Preflight = new PreflightOptions { MinimumFreeSpaceBytes = 0 },
                ProcessExitTimeoutMs = 3000,
                Watchdog = new WatchdogOptions { Enabled = false }
            }
        };
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config, ConfigLoader.JsonOptions));
        var (_, paths) = ConfigLoader.Load(configPath);
        var executable = Path.GetFileName(Environment.ProcessPath!);
        foreach (var name in new[] { "a-timeout", "b-success" })
        {
            var package = Path.Combine(paths.Pending, name);
            Directory.CreateDirectory(package);
            CopyRunnerFixture(package);
            var job = new JobDefinition
            {
                Id = name,
                TargetOs = OperatingSystem.IsWindows() ? "windows" : "linux",
                Executable = executable,
                Arguments = ["--fake-xemu", "--fake-runtime-ms", "10000"],
                TimeoutSeconds = 5,
                Plan = [new JobStep { Type = "quit" }]
            };
            if (name == "a-timeout")
            {
                job.Diagnostics = [new DiagnosticRecipe
                {
                    Id = "bounded-tool", Type = "external", DurationMs = 100,
                    PauseBefore = false, ResumeDuring = false, PauseAfter = false,
                    ToolExecutable = Environment.ProcessPath!,
                    ToolArguments = ["--fake-diagnostic-delay"]
                }];
                job.Plan = [new JobStep { Type = "diagnostic", DiagnosticId = "bounded-tool" }];
            }
            if (name == "b-success" && standaloneNext)
            {
                job.TargetKind = JobTargetKind.Process;
                job.Arguments = ["--fixture-process", "success"];
                job.Plan = [];
            }
            await File.WriteAllTextAsync(Path.Combine(package, "job.json"),
                JsonSerializer.Serialize(job, ConfigLoader.JsonOptions));
        }
        var engine = new RunnerEngine(config, paths);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await engine.RunAsync(once: false, maxJobs: 2, cancellationToken: deadline.Token);
        var snapshot = engine.State.Snapshot();
        Assert(snapshot.JobsFinished == 2 && snapshot.FailedJobs == 1,
            "Diagnostic failure stopped queue processing or became a pass.");
        Assert(Directory.GetDirectories(paths.Testing).Length == 0 &&
            Directory.GetDirectories(paths.Tested).Length == 2,
            "Finished diagnostic failure was not archived alongside the next package.");
        var results = Directory.GetDirectories(paths.Results)
            .Select(directory => Path.Combine(directory, "result.json"))
            .Select(path => JsonDocument.Parse(File.ReadAllText(path))).ToArray();
        try
        {
            var failed = results.Single(result => result.RootElement.GetProperty("job").GetString() == "a-timeout");
            Assert(failed.RootElement.GetProperty("status").GetString() == "plan_failed",
                "A settled diagnostic timeout was classified as failed cleanup.");
            Assert(failed.RootElement.GetProperty("diagnostics").GetArrayLength() == 1,
                "Previous run lost its finalized diagnostic evidence.");
            var success = results.Single(result => result.RootElement.GetProperty("job").GetString() == "b-success");
            Assert(success.RootElement.GetProperty("status").GetString() == "completed" &&
                success.RootElement.GetProperty("diagnostics").GetArrayLength() == 0,
                "Next run inherited the preceding xemu diagnostic records.");
            var diagnostic = Directory.GetFiles(paths.Results, "result.json", SearchOption.AllDirectories)
                .Single(path => path.Contains("bounded-tool"));
            using var recorded = JsonDocument.Parse(File.ReadAllText(diagnostic));
            Assert(recorded.RootElement.GetProperty("Status").GetString() == "failed",
                "The diagnostic timeout was not retained as failed evidence.");
            var detail = recorded.RootElement.GetProperty("Detail").GetString() ?? "";
            Assert(detail.Contains("TimeoutException", StringComparison.Ordinal) &&
                detail.Contains("Diagnostic tool or output drain exceeded", StringComparison.Ordinal),
                "An unrelated diagnostic failure satisfied the timeout regression.");
        }
        finally { foreach (var result in results) result.Dispose(); }
    });

    await Check("one-shot runner processes at most one queued package", async () =>
    {
        var fixture = Path.Combine(root, "one-shot");
        var configPath = Path.Combine(fixture, "runner.json");
        Directory.CreateDirectory(fixture);

        var config = new RunnerConfig
        {
            Workspace = "workspace",
            Queue = new QueueOptions
            {
                PackageStabilityMs = 100,
                ScanIntervalMs = 25
            },
            Http = new HttpOptions { Enabled = false },
            Monitoring = new MonitoringOptions { Enabled = false },
            XemuControl = new XemuControlOptions
            {
                Enabled = true,
                ConnectTimeoutMs = 3000,
                InputProvider = "unavailable"
            },
            Reliability = new ReliabilityOptions
            {
                Preflight = new PreflightOptions { MinimumFreeSpaceBytes = 0 },
                ProcessExitTimeoutMs = 3000,
                Watchdog = new WatchdogOptions { Enabled = false }
            }
        };

        await File.WriteAllTextAsync(
            configPath,
            JsonSerializer.Serialize(config, ConfigLoader.JsonOptions));

        var (_, paths) = ConfigLoader.Load(configPath);
        var executable = Path.GetFileName(Environment.ProcessPath!);

        foreach (var name in new[] { "job-a", "job-b" })
        {
            var package = Path.Combine(paths.Pending, name);
            Directory.CreateDirectory(package);
            CopyRunnerFixture(package);

            await File.WriteAllTextAsync(
                Path.Combine(package, "job.json"),
                JsonSerializer.Serialize(
                    new JobDefinition
                    {
                        Id = name,
                        TargetOs = OperatingSystem.IsWindows() ? "windows" : "linux",
                        Executable = executable,
                        Arguments = ["--fake-xemu", "--fake-runtime-ms", "5000"],
                        TimeoutSeconds = 3,
                        Plan = [new JobStep { Type = "quit" }]
                    },
                    ConfigLoader.JsonOptions));
        }

        var engine = new XemuTestRunner.Runtime.RunnerEngine(config, paths);
        await engine.RunAsync(
            once: true,
            maxJobs: 1,
            cancellationToken: CancellationToken.None);

        var snapshot = engine.State.Snapshot();
        Assert(snapshot.JobsFinished == 1, $"One-shot finished {snapshot.JobsFinished} jobs.");
        Assert(snapshot.FailedJobs == 0, "One-shot fixture failed.");
        Assert(Directory.GetDirectories(paths.Tested).Length == 1,
            "One-shot did not archive exactly one package.");
        Assert(Directory.GetDirectories(paths.Pending).Length == 1,
            "One-shot consumed more than one pending package.");
    });

    await Check("advertised HTTP address never reports wildcard when an override is supplied", () =>
    {
        var endpoint = NetworkEndpointResolver.Resolve(new HttpOptions
        {
            BindAddress = "0.0.0.0",
            AdvertiseAddress = "192.0.2.44",
            Port = 9368
        });
        Assert(endpoint.ListenAddress == "0.0.0.0", "Listen address changed unexpectedly.");
        Assert(endpoint.AdvertisedAddress == "192.0.2.44", "Advertised override was ignored.");
        Assert(endpoint.Url == "http://192.0.2.44:9368", "Advertised URL is incorrect.");
        return Task.CompletedTask;
    });

    await Check("system telemetry produces baseline process and memory values", async () =>
    {
        using var provider = new SystemMetricProvider();
        using var current = Process.GetCurrentProcess();
        _ = provider.Sample(current, processIo: true);
        await Task.Delay(150);
        var sample = provider.Sample(current, processIo: true);

        Assert(sample.HostMemoryTotalBytes is > 0, "Host memory total is unavailable.");
        Assert(sample.HostMemoryAvailableBytes is >= 0, "Host available memory is unavailable.");
        Assert(sample.ProcessWorkingSetBytes is > 0, "Process working set is unavailable.");
        Assert(sample.ProcessPrivateBytes is > 0, "Process private memory is unavailable.");
        Assert(sample.ProcessCpuPercent is >= 0, "Process CPU did not become available after the priming sample.");
        Assert(sample.HostCpuPercent is >= 0, "Host CPU did not become available after the priming sample.");
        Assert(sample.Errors.Count == 0, "Telemetry provider reported: " + string.Join(" | ", sample.Errors));
    });

    await Check("runner completes a queued QMP job and retains its evidence", async () =>
    {
        var fixture = Path.Combine(root, "runner-integration");
        var configPath = Path.Combine(fixture, "runner.json");
        var httpPort = AllocateTcpPort();
        var config = new RunnerConfig
        {
            Workspace = "workspace",
            Http = new HttpOptions { Enabled = true, BindAddress = "127.0.0.1", Port = httpPort },
            Monitoring = new MonitoringOptions
            {
                Enabled = true,
                IntervalMs = 25,
                FlushIntervalMs = 50,
                BufferCapacity = 64,
                Gpu = new GpuOptions { Enabled = false }
            },
            XemuControl = new XemuControlOptions
            {
                Enabled = true,
                ConnectTimeoutMs = 3000,
                ScreenshotTimeoutMs = 3000,
                InputProvider = "unavailable",
                ScreenshotProvider = "auto",
                ScreenshotExecutable = Environment.ProcessPath!,
                ScreenshotArguments = ["--fake-screenshot-delayed", "{path}"]
            },
            Reliability = new ReliabilityOptions
            {
                Preflight = new PreflightOptions { MinimumFreeSpaceBytes = 0 },
                ProcessExitTimeoutMs = 3000,
                Watchdog = new WatchdogOptions
                {
                    Enabled = true,
                    StartupGraceMs = 0,
                    IntervalMs = 100,
                    RequestTimeoutMs = 500,
                    FailureThreshold = 3
                }
            }
        };

        Directory.CreateDirectory(fixture);
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config, ConfigLoader.JsonOptions));
        var (_, paths) = ConfigLoader.Load(configPath);
        var package = Path.Combine(paths.Pending, "fake-qmp-job");
        Directory.CreateDirectory(package);
        CopyRunnerFixture(package);

        var executable = Path.GetFileName(Environment.ProcessPath!);
        var job = new JobDefinition
        {
            Id = "fake-qmp-job",
            TargetOs = OperatingSystem.IsWindows() ? "windows" : "linux",
            Executable = executable,
            // Keep the fixture alive beyond the runner's own bounded timeout.
            // A successful test exits promptly through the final QMP quit; a
            // broken plan is still terminated by TimeoutSeconds below.
            Arguments = ["--fake-xemu", "--fake-runtime-ms", "15000"],
            TimeoutSeconds = 5,
            StartPaused = true,
            Operations = new OperationPolicyDefinition { Mode = "benchmark", AllowPreview = true },
            Experiment = new ExperimentDefinition { Id = "runner-integration", Variant = "fixture" },
            Plan =
            [
                new JobStep { Type = "screenshot", Name = "integration-frame" },
                new JobStep { Type = "resume" },
                new JobStep { Type = "wait", DelayMs = 1000 },
                new JobStep { Type = "pause" },
                new JobStep { Type = "resume" },
                new JobStep { Type = "quit" }
            ]
        };
        await File.WriteAllTextAsync(
            Path.Combine(package, "job.json"),
            JsonSerializer.Serialize(job, ConfigLoader.JsonOptions));

        var engine = new XemuTestRunner.Runtime.RunnerEngine(config, paths);
        var runTask = engine.RunAsync(once: true, CancellationToken.None);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{httpPort}") };
            using var statusJson = await WaitForActiveRunAsync(client, TimeSpan.FromSeconds(3));
            Assert(statusJson.RootElement.GetProperty("RunId").ValueKind == JsonValueKind.String,
                "The live HTTP status did not identify the active run.");

            using var comparison = await client.GetAsync("/api/v1/experiments/runner-integration");
            Assert(comparison.StatusCode == HttpStatusCode.OK,
                $"Documented experiment API returned {(int)comparison.StatusCode}, expected 200.");
            using var comparisonJson = JsonDocument.Parse(await comparison.Content.ReadAsStringAsync());
            Assert(comparisonJson.RootElement.GetProperty("ExperimentId").GetString() == "runner-integration",
                "Experiment API did not preserve the requested experiment identity.");

            using var input = await client.PostAsync(
                "/api/v1/input/press",
                new StringContent("{\"Button\":\"A\",\"DurationMs\":100}", Encoding.UTF8, "application/json"));
            Assert(input.StatusCode == HttpStatusCode.Conflict,
                $"Blocked input returned {(int)input.StatusCode}, expected 409.");
            Assert(input.Headers.ConnectionClose == true,
                "A rejected request body must close the HTTP connection without draining it.");
            Assert((await input.Content.ReadAsStringAsync()).Contains("operation_blocked", StringComparison.Ordinal),
                "Blocked input did not return the structured operation policy error.");

            using var upload = await client.PutAsync(
                "/api/v1/files/blocked.txt",
                new StringContent("must-not-land", Encoding.UTF8, "application/octet-stream"));
            Assert(upload.StatusCode == HttpStatusCode.Conflict,
                $"Blocked upload returned {(int)upload.StatusCode}, expected 409.");
            Assert((await upload.Content.ReadAsStringAsync()).Contains("operation_blocked", StringComparison.Ordinal),
                "Blocked upload did not return the structured operation policy error.");
            Assert(!File.Exists(Path.Combine(paths.FileRoot, "blocked.txt")),
                "The blocked upload unexpectedly created its destination file.");
        }
        finally
        {
            await runTask;
        }

        var results = Directory.GetDirectories(paths.Results);
        Assert(results.Length == 1, $"Expected one result, found {results.Length}.");
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(results[0], "result.json")));
        Assert(result.RootElement.GetProperty("status").GetString() == "completed", result.RootElement.ToString());
        Assert(result.RootElement.GetProperty("monitoring").GetProperty("samples").GetInt64() > 0,
            "No telemetry samples were retained.");
        Assert(File.Exists(Path.Combine(results[0], "screenshots", "integration-frame.png")),
            "The screenshot fallback was not retained.");
        Assert(Directory.GetDirectories(paths.Tested).Length == 1 && Directory.GetDirectories(paths.Testing).Length == 0,
            "The completed package was not archived exactly once.");
    });

    await Check("release identity is available to the CLI and evidence", () =>
    {
        Assert(!string.IsNullOrWhiteSpace(XemuTestRunner.ApplicationInfo.DisplayVersion),
            "The runner has no display version.");
        Assert(XemuTestRunner.ApplicationInfo.DisplayVersion != "0.0.0",
            "The runner still exposes the placeholder version.");
        return Task.CompletedTask;
    });
}
finally { Directory.Delete(root, recursive: true); }
Console.WriteLine($"Failures: {failures}");
return failures == 0 ? 0 : 1;

static void CopyRunnerFixture(string destination)
{
    var source = AppContext.BaseDirectory;
    foreach (var file in Directory.EnumerateFiles(source))
        File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
    if (!OperatingSystem.IsWindows())
        File.SetUnixFileMode(
            Path.Combine(destination, Path.GetFileName(Environment.ProcessPath!)),
            UnixFileMode.UserRead | UnixFileMode.UserExecute);
}

static int AllocateTcpPort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
    finally { listener.Stop(); }
}

static async Task ServeReadinessAfterTimeoutAsync(TcpListener listener, CancellationToken ct)
{
    // Accept the first connection without a greeting until the real client
    // exhausts its attempt timeout and closes it. The next connection is ready.
    using (var stalled = await listener.AcceptTcpClientAsync(ct))
    {
        var bytes = new byte[1];
        while (await stalled.GetStream().ReadAsync(bytes, ct) != 0) { }
    }
    for (var connection = 0; connection < 2; connection++)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        using var reader = new StreamReader(client.GetStream());
        await using var writer = new StreamWriter(client.GetStream()) { AutoFlush = true };
        await writer.WriteLineAsync("{\"QMP\":{}}");
        for (var command = 0; command < 2; command++)
        {
            using var request = JsonDocument.Parse(await reader.ReadLineAsync(ct)
                ?? throw new IOException("Readiness client disconnected before its query."));
            var name = request.RootElement.GetProperty("execute").GetString();
            if (name != (command == 0 ? "qmp_capabilities" : "query-status"))
                throw new InvalidDataException("Unexpected readiness command: " + name);
            var id = request.RootElement.GetProperty("id").GetInt32();
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                id,
                @return = new { status = "paused" }
            }));
        }
    }
}

static void WriteRgbPng(
    string path,
    int width,
    int height,
    Func<int, int, (byte Red, byte Green, byte Blue)> pixel)
{
    using var file = File.Create(path);
    file.Write([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

    var header = new byte[13];
    BinaryPrimitives.WriteInt32BigEndian(header, width);
    BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
    header[8] = 8;
    header[9] = 2;
    WritePngChunk(file, "IHDR", header);

    using var raw = new MemoryStream();
    for (var y = 0; y < height; y++)
    {
        raw.WriteByte(0);
        for (var x = 0; x < width; x++)
        {
            var value = pixel(x, y);
            raw.WriteByte(value.Red);
            raw.WriteByte(value.Green);
            raw.WriteByte(value.Blue);
        }
    }
    raw.Position = 0;
    using var compressed = new MemoryStream();
    using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true))
        raw.CopyTo(zlib);
    WritePngChunk(file, "IDAT", compressed.ToArray());
    WritePngChunk(file, "IEND", []);
}

static void WritePngChunk(Stream stream, string type, byte[] data)
{
    Span<byte> length = stackalloc byte[4];
    BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
    stream.Write(length);
    stream.Write(Encoding.ASCII.GetBytes(type));
    stream.Write(data);
    stream.Write([0, 0, 0, 0]);
}

static async Task<JsonDocument> WaitForActiveRunAsync(HttpClient client, TimeSpan timeout)
{
    using var deadline = new CancellationTokenSource(timeout);
    Exception? last = null;
    while (!deadline.IsCancellationRequested)
    {
        try
        {
            using var response = await client.GetAsync("/api/v1/status", deadline.Token);
            response.EnsureSuccessStatusCode();
            var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
            if (document.RootElement.TryGetProperty("RunId", out var runId) && runId.ValueKind == JsonValueKind.String)
                return document;
            document.Dispose();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            last = ex;
        }
        await Task.Delay(25, deadline.Token);
    }
    throw new TimeoutException("HTTP endpoint did not expose an active run.", last);
}
