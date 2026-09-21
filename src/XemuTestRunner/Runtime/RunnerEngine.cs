using System.Diagnostics;
using System.Security.Cryptography;
using XemuTestRunner.Config;
using XemuTestRunner.Control;
using XemuTestRunner.Diagnostics;
using XemuTestRunner.Monitoring;
using XemuTestRunner.Networking;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Runtime;

public sealed class RunnerEngine
{
    private readonly RunnerConfig _config;
    private readonly RunnerPaths _paths;
    private readonly RunnerState _state = new();
    private readonly JobQueue _queue;
    private readonly XemuControlManager _control;
    private readonly ActivityHub _activity = new();
    private readonly DiagnosticHub _diagnostics;

    public RunnerState State => _state;

    public RunnerEngine(RunnerConfig config, RunnerPaths paths)
    {
        _config = config;
        _paths = paths;
        _queue = new(config, paths);
        _control = new(config.XemuControl);
        _diagnostics = new(config.Diagnostics, _control, _state, _activity);
    }

    public async Task RunAsync(bool once, CancellationToken cancellationToken)
    {
        _queue.EnsureDirectories();
        using var owner = WorkspaceLease.Acquire(_paths.Workspace);
        using var queueOwner = WorkspaceLease.Acquire(_paths.Testing);
        _queue.RecoverInterrupted();
        _state.SetQueue(_queue.Snapshot());

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var server = new EmbeddedHttpServer(
            _config.Http,
            _config.Ui,
            _paths,
            _state,
            _queue,
            _control,
            lifetime.Cancel)
        {
            Reliability = _config.Reliability,
            Activity = _activity,
            Diagnostics = _diagnostics
        };
        var serverTask = server.RunAsync(lifetime.Token);

        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                if (serverTask.IsFaulted)
                    await serverTask.ConfigureAwait(false);

                if (_queue.GetTestingJobs().Count > 0)
                {
                    _state.SetPhase("interrupted");
                    if (once)
                        break;
                    await Task.Delay(_config.Queue.ScanIntervalMs, lifetime.Token).ConfigureAwait(false);
                    continue;
                }

                var package = _queue.TryClaimNext();
                _state.SetQueue(_queue.Snapshot());
                if (package is null)
                {
                    _state.SetPhase(once ? "finished" : "idle");
                    if (once)
                        break;
                    await Task.Delay(_config.Queue.ScanIntervalMs, lifetime.Token).ConfigureAwait(false);
                    continue;
                }

                await ExecuteJobAsync(package, serverTask, lifetime.Token).ConfigureAwait(false);
                _state.SetQueue(_queue.Snapshot());
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            _state.SetPhase("stopped");
        }
        catch
        {
            _state.SetPhase("faulted");
            throw;
        }
        finally
        {
            lifetime.Cancel();
            _diagnostics.Detach();
            _activity.Detach();
            _control.End();
            try { await serverTask.ConfigureAwait(false); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }

            if (cancellationToken.IsCancellationRequested)
                _state.SetPhase("stopped");
        }
    }

    private async Task ExecuteJobAsync(string package, Task serverTask, CancellationToken ct)
    {
        var startedUtc = DateTimeOffset.UtcNow;
        var runId = $"{startedUtc:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}";
        var resultDirectory = Path.Combine(_paths.Results, runId);
        Directory.CreateDirectory(resultDirectory);

        var attempt = new AttemptRecord { RunId = runId, Attempt = 1 };
        JobDefinition? job = null;
        PreflightReport? preflight = null;
        WatchdogTrip? watchdogTrip = null;
        DiagnosticResult? automaticBundle = null;
        string? automaticBundleError = null;
        string status = "invalid_job";
        string? detail = null;
        string? failureCaptureError = null;
        int? exitCode = null;
        bool started = false;
        bool exited = false;
        bool componentStuck = false;
        IReadOnlyList<string> gpuProviders = [];

        TargetLaunch? launch = null;
        Process? process = null;
        using var tasksCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var logsCts = new CancellationTokenSource();
        using var activity = new RunActivity(resultDirectory);

        MetricCollector? collector = null;
        Task? monitor = null;
        Task? plan = null;
        Task? watchdog = null;
        Task? stdout = null;
        Task? stderr = null;
        FileStream? stdoutFile = null;
        FileStream? stderrFile = null;
        var effectiveArguments = new List<string>();

        try
        {
            attempt.Attempt = (AttemptJournal.Read(package)?.Attempt ?? 0) + 1;
            AttemptJournal.Write(package, attempt);
            File.Copy(Path.Combine(package, "job.json"), Path.Combine(resultDirectory, "job.json"));

            _state.SetPhase("preflight");
            job = JobDefinition.LoadPackage(package);
            preflight = await Preflight.CheckAsync(
                job,
                package,
                _paths.Results,
                _config.Reliability.Preflight,
                ct).ConfigureAwait(false);

            if (job.Plan.Count > 0 && !_config.XemuControl.Enabled)
                preflight = AddPreflightFailure(preflight, "control", "Plan actions require XemuControl.");

            if (_config.XemuControl.Enabled &&
                job.Arguments.Any(argument =>
                    argument.Equals("-qmp", StringComparison.OrdinalIgnoreCase) ||
                    argument.StartsWith("-qmp=", StringComparison.OrdinalIgnoreCase)))
                preflight = AddPreflightFailure(preflight, "qmp", "The runner owns -qmp.");

            if (job.SnapshotName is not null &&
                job.Arguments.Any(argument =>
                    argument.Equals("-loadvm", StringComparison.OrdinalIgnoreCase) ||
                    argument.StartsWith("-loadvm=", StringComparison.OrdinalIgnoreCase)))
                preflight = AddPreflightFailure(preflight, "snapshot", "Use SnapshotName instead of supplying -loadvm manually.");

            if (job.StartPaused &&
                job.Arguments.Any(argument => argument.Equals("-S", StringComparison.OrdinalIgnoreCase)))
                preflight = AddPreflightFailure(preflight, "start_paused", "Use StartPaused instead of supplying -S manually.");

            if (job.StartPaused && !_config.XemuControl.Enabled)
                preflight = AddPreflightFailure(preflight, "start_paused", "StartPaused requires XemuControl so the runner can resume the VM.");

            if (job.RequireInput && !_config.XemuControl.Enabled)
                preflight = AddPreflightFailure(preflight, "input", "RequireInput requires XemuControl.");

            if (job.LaunchMode.Equals("renderdoc", StringComparison.OrdinalIgnoreCase))
            {
                var renderDocPython = await _diagnostics.Tools.ProbeRenderDocPythonAsync(ct).ConfigureAwait(false);
                if (!renderDocPython.Available)
                    preflight = AddPreflightFailure(preflight, "renderdoc_python", renderDocPython.Detail);
            }

            AtomicJson.Write(Path.Combine(resultDirectory, "preflight.json"), preflight);
            if (!preflight.Passed)
            {
                status = "preflight_failed";
                detail = "See preflight.json.";
            }
            else
            {
                var qmpPort = _config.XemuControl.Enabled ? _control.AllocateQmpPort() : 0;
                effectiveArguments.AddRange(job.Arguments);

                if (job.StartPaused)
                    effectiveArguments.Add("-S");
                if (!string.IsNullOrWhiteSpace(job.SnapshotName))
                {
                    effectiveArguments.Add("-loadvm");
                    effectiveArguments.Add(job.SnapshotName);
                }
                if (_config.XemuControl.Enabled)
                {
                    effectiveArguments.Add("-qmp");
                    effectiveArguments.Add($"tcp:{_config.XemuControl.QmpHost}:{qmpPort},server=on,wait=off");
                }

                var executable = JobDefinition.ResolveInsidePackage(package, job.Executable);
                var workingDirectory = JobDefinition.ResolveInsidePackage(package, job.WorkingDirectory ?? ".");

                AtomicJson.Write(Path.Combine(resultDirectory, "launch.json"), new
                {
                    runId,
                    attempt = attempt.Attempt,
                    job = job.Id,
                    launchMode = job.LaunchMode,
                    snapshotName = job.SnapshotName,
                    startPaused = job.StartPaused,
                    executable = job.Executable,
                    executableSha256 = preflight.ExecutableSha256,
                    jobSha256 = Convert.ToHexString(
                        SHA256.HashData(File.ReadAllBytes(
                            Path.Combine(resultDirectory, "job.json")))).ToLowerInvariant(),
                    arguments = effectiveArguments,
                    workingDirectory = job.WorkingDirectory ?? ".",
                    host = HostInfo()
                });

                attempt.Phase = "starting";
                AttemptJournal.Write(package, attempt);
                status = "start_failed";

                launch = await TargetLaunch.StartAsync(
                    _config.Diagnostics,
                    job,
                    executable,
                    workingDirectory,
                    effectiveArguments,
                    resultDirectory,
                    ct).ConfigureAwait(false);
                process = launch.Process;
                started = true;

                if (launch.StandardOutput is not null && launch.StandardError is not null)
                {
                    stdoutFile = OpenLog(Path.Combine(resultDirectory, "stdout.log"));
                    stderrFile = OpenLog(Path.Combine(resultDirectory, "stderr.log"));
                    stdout = PumpLogAsync(launch.StandardOutput, stdoutFile, logsCts.Token);
                    stderr = PumpLogAsync(launch.StandardError, stderrFile, logsCts.Token);
                }
                else
                {
                    await File.WriteAllTextAsync(
                        Path.Combine(resultDirectory, "stdout.log"),
                        "xemu was launched by the RenderDoc bridge; target stdout is not exposed by RenderDoc ExecuteAndInject." + Environment.NewLine,
                        ct).ConfigureAwait(false);
                    await File.WriteAllTextAsync(
                        Path.Combine(resultDirectory, "stderr.log"),
                        "RenderDoc bridge diagnostics are stored under diagnostics/_renderdoc-session/." + Environment.NewLine,
                        ct).ConfigureAwait(false);
                }

                attempt.ProcessId = process.Id;
                try { attempt.ProcessStartedUtc = process.StartTime.ToUniversalTime(); }
                catch (InvalidOperationException) when (HasExited(process)) { }
                attempt.Phase = HasExited(process) ? "exited" : "running";
                AttemptJournal.Write(package, attempt);

                _state.BeginJob(job.Id, runId, process.Id);
                _activity.Attach(runId, activity);

                // Start host/process telemetry as soon as the target exists. QMP,
                // input, screenshots and diagnostics are optional control layers;
                // they must not prevent CPU/RAM/GPU evidence from being collected.
                if (_config.Monitoring.Enabled)
                {
                    collector = new MetricCollector(_config.Monitoring, _state.SetLatestMetric);
                    gpuProviders = collector.GpuProviders.ToArray();
                    monitor = collector.RunAsync(
                        process,
                        Path.Combine(resultDirectory, "metrics.csv"),
                        tasksCts.Token);
                }

                if (_config.XemuControl.Enabled)
                {
                    try
                    {
                        _control.Begin(process, resultDirectory, qmpPort);
                        await _control.WaitUntilReadyAsync(ct).ConfigureAwait(false);
                        await _control.RefreshPauseStateAsync(ct).ConfigureAwait(false);
                        if (job.StartPaused && !_control.Snapshot().Paused)
                            throw new InvalidOperationException("xemu did not remain paused after StartPaused launch/snapshot restore.");
                        _state.SetPhase(_control.Snapshot().Paused ? "paused" : "running");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        status = "control_error";
                        detail = "xemu launched, but runner control initialization failed: " + ex;
                        throw;
                    }
                }

                if (job.RequireInput && !_control.Snapshot().InputAvailable)
                {
                    status = "control_error";
                    detail = "xemu launched, but the configured controller input provider is unavailable.";
                    throw new InvalidOperationException(detail);
                }

                _diagnostics.Attach(
                    runId,
                    process,
                    launch.RenderDoc,
                    resultDirectory,
                    job,
                    tasksCts.Token);

                if (job.Plan.Count > 0)
                {
                    plan = _control.ExecutePlanAsync(
                        job.Plan,
                        tasksCts.Token,
                        async (id, token) =>
                        {
                            _ = await _diagnostics.RunByIdAsync(id, token).ConfigureAwait(false);
                        });
                }

                Task<WatchdogTrip>? watch = null;
                if (_config.Reliability.Watchdog.Enabled && _config.XemuControl.Enabled)
                {
                    watch = new ResponsivenessWatchdog(_config.Reliability.Watchdog).RunAsync(
                        async token => { _ = await _control.QueryStatusAsync(token).ConfigureAwait(false); },
                        tasksCts.Token);
                    watchdog = watch;
                }

                var exitTask = process.WaitForExitAsync();
                var cancelTask = Task.Delay(Timeout.Infinite, ct);
                var timeoutTask = job.TimeoutSeconds > 0
                    ? _control.DelayTestTimeAsync(checked(job.TimeoutSeconds * 1000), tasksCts.Token)
                    : Task.Delay(Timeout.Infinite, tasksCts.Token);

                var waiting = new List<Task> { exitTask, cancelTask, timeoutTask };
                if (_config.Http.Enabled)
                    waiting.Add(serverTask);
                if (plan is not null)
                    waiting.Add(plan);
                if (monitor is not null)
                    waiting.Add(monitor);
                if (watch is not null)
                    waiting.Add(watch);

                while (true)
                {
                    var winner = await Task.WhenAny(waiting).ConfigureAwait(false);

                    if (ct.IsCancellationRequested)
                    {
                        status = "cancelled";
                        break;
                    }

                    if (exitTask.IsCompleted)
                    {
                        await exitTask.ConfigureAwait(false);
                        exited = true;
                        exitCode = process.ExitCode;
                        status = exitCode != 0
                            ? "failed"
                            : plan is not null && !plan.IsCompletedSuccessfully && !_control.QuitRequested
                                ? "incomplete_plan"
                                : "completed";
                        break;
                    }

                    if (winner == plan)
                    {
                        try { await plan!.ConfigureAwait(false); }
                        catch (Exception ex)
                        {
                            status = "plan_failed";
                            detail = ex.ToString();
                            break;
                        }
                        waiting.Remove(winner);
                        continue;
                    }

                    if (winner == timeoutTask)
                    {
                        status = "timeout";
                        detail = $"Exceeded {job.TimeoutSeconds} unpaused seconds.";
                        break;
                    }

                    if (winner == watch)
                    {
                        watchdogTrip = await watch!.ConfigureAwait(false);
                        status = "unresponsive";
                        detail = "QMP watchdog threshold reached. This is not a guest-progress verdict.";
                        try
                        {
                            automaticBundle = await _diagnostics.AutoHangBundleAsync(
                                detail,
                                CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            automaticBundleError = ex.Message;
                        }
                        break;
                    }

                    await winner.ConfigureAwait(false);
                    throw new IOException("A required runner component stopped unexpectedly.");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status = "cancelled";
        }
        catch (Exception ex)
        {
            if (started && status is "start_failed" or "invalid_job")
                status = "runner_error";
            detail ??= ex.ToString();
        }
        finally
        {
            tasksCts.Cancel();

            if (!await _diagnostics.WaitForIdleAsync(
                    _config.Reliability.ProcessExitTimeoutMs).ConfigureAwait(false))
            {
                componentStuck = true;
                status = "cleanup_failed";
                detail = (detail ?? "") + " Diagnostic operation did not stop after run cancellation.";
            }

            if (process is not null && started)
            {
                exited = HasExited(process);
                var preserveTarget =
                    !exited &&
                    _config.Reliability.PreserveTargetOnRunnerError &&
                    status is "runner_error" or "control_error";

                if (!preserveTarget &&
                    !exited &&
                    status is not ("completed" or "cancelled" or "unresponsive"))
                {
                    try
                    {
                        automaticBundle = await _diagnostics.AutoFailureBundleAsync(
                            status,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        automaticBundleError = ex.Message;
                    }
                }

                if (!preserveTarget &&
                    !exited &&
                    _config.XemuControl.Enabled &&
                    automaticBundle is null &&
                    status is not ("cancelled" or "unresponsive"))
                {
                    using var screenshotDeadline = new CancellationTokenSource(
                        _config.XemuControl.ScreenshotTimeoutMs);
                    try
                    {
                        _ = await _control.CaptureScreenshotAsync(
                            "failure",
                            screenshotDeadline.Token,
                            record: false).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        failureCaptureError = ex.Message;
                    }
                }

                if (!preserveTarget && !exited)
                    exited = await StopProcessAsync(process).ConfigureAwait(false);

                if (preserveTarget)
                {
                    detail = (detail ?? status) +
                        " Target intentionally left running because Reliability.PreserveTargetOnRunnerError is enabled. " +
                        "The package remains in Testing for inspection; stop xemu manually before retrying.";
                }
                else if (exited)
                {
                    exitCode = process.ExitCode;
                    attempt.Phase = "exited";
                    AttemptJournal.Write(package, attempt);
                }
                else
                {
                    status = "cleanup_failed";
                    detail = "Target process could not be confirmed stopped. Package retained in Testing.";
                }
            }

            await SettleAsync(plan, "plan").ConfigureAwait(false);
            await SettleAsync(watchdog, "watchdog").ConfigureAwait(false);
            _diagnostics.Detach();
            _activity.Detach();
            _control.End();
            await SettleAsync(monitor, "monitor").ConfigureAwait(false);

            if (stdout is not null || stderr is not null)
            {
                try
                {
                    await Task.WhenAll(
                        stdout ?? Task.CompletedTask,
                        stderr ?? Task.CompletedTask).WaitAsync(
                            TimeSpan.FromMilliseconds(_config.Reliability.ProcessExitTimeoutMs))
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logsCts.Cancel();
                    detail = (detail ?? "") + " Log drain: " + ex.Message;
                }
                await SettleAsync(stdout, "stdout").ConfigureAwait(false);
                await SettleAsync(stderr, "stderr").ConfigureAwait(false);
            }

            collector?.Dispose();
            if (stdoutFile is not null)
                await stdoutFile.DisposeAsync().ConfigureAwait(false);
            if (stderrFile is not null)
                await stderrFile.DisposeAsync().ConfigureAwait(false);
            if (launch is not null)
                await launch.DisposeAsync().ConfigureAwait(false);
        }

        var quality = activity.Snapshot();
        activity.Dispose();
        AtomicJson.Write(Path.Combine(resultDirectory, "result.json"), new
        {
            runId,
            job = job?.Id ?? Path.GetFileName(package),
            jobPackage = Path.GetFileName(package),
            attempt = attempt.Attempt,
            status,
            detail,
            startedUtc,
            endedUtc = DateTimeOffset.UtcNow,
            durationMs = (DateTimeOffset.UtcNow - startedUtc).TotalMilliseconds,
            processId = attempt.ProcessId,
            exitCode,
            launchMode = job?.LaunchMode,
            snapshotName = job?.SnapshotName,
            executable = job?.Executable,
            executableSha256 = preflight?.ExecutableSha256,
            arguments = effectiveArguments,
            watchdog = watchdogTrip,
            failureCaptureError,
            automaticDiagnostic = automaticBundle,
            automaticDiagnosticError = automaticBundleError,
            diagnostics = _diagnostics.Snapshot().Completed,
            operatorActivity = quality,
            comparisonStatus = quality.Intervened ? "operator_intervened" : "not_evaluated",
            host = HostInfo(),
            monitoring = collector is null ? null : new
            {
                intervalMs = _config.Monitoring.IntervalMs,
                samples = collector.SampleCount,
                overruns = collector.OverrunCount,
                droppedWriteSamples = collector.DroppedWriteSamples,
                gpuProviders
            }
        });

        if ((started && !exited) || componentStuck)
            throw new IOException(detail);

        attempt.Phase = "finalized";
        AttemptJournal.Write(package, attempt);
        _queue.Complete(package);
        _state.EndJob(status, job?.Id ?? Path.GetFileName(package));

        async Task SettleAsync(Task? task, string component)
        {
            if (task is null)
                return;
            try
            {
                await task.WaitAsync(
                    TimeSpan.FromMilliseconds(_config.Reliability.ProcessExitTimeoutMs))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (TimeoutException ex)
            {
                componentStuck = true;
                status = "cleanup_failed";
                detail = (detail ?? "") + $" {component} did not stop: {ex.Message}";
            }
            catch (Exception ex)
            {
                detail = (detail ?? "") + $" {component}: {ex.Message}";
                if (status == "completed")
                    status = "runner_error";
            }
        }
    }

    private static PreflightReport AddPreflightFailure(
        PreflightReport report,
        string name,
        string detail) =>
        new(false, report.ExecutableSha256, [.. report.Checks, new(name, false, detail)]);

    private async Task<bool> StopProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }

        try
        {
            await process.WaitForExitAsync().WaitAsync(
                TimeSpan.FromMilliseconds(_config.Reliability.ProcessExitTimeoutMs))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { }

        return HasExited(process);
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return false; }
    }

    private static FileStream OpenLog(string path) =>
        new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task PumpLogAsync(
        Stream source,
        FileStream destination,
        CancellationToken ct)
    {
        var buffer = new byte[65536];
        while (true)
        {
            var count = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (count == 0)
                break;
            await destination.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            await destination.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    private static object HostInfo() => new
    {
        runnerVersion = ApplicationInfo.DisplayVersion,
        machine = Environment.MachineName,
        os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
        processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        processorCount = Environment.ProcessorCount,
        dotnet = Environment.Version.ToString()
    };
}
