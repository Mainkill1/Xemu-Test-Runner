using System.Diagnostics;
using System.Security.Cryptography;
using XemuTestRunner.Config;
using XemuTestRunner.Control;
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
    public RunnerState State => _state;
    public RunnerEngine(RunnerConfig config, RunnerPaths paths)
    {
        _config = config; _paths = paths; _queue = new(config, paths); _control = new(config.XemuControl);
    }
    public async Task RunAsync(bool once, CancellationToken cancellationToken)
    {
        _queue.EnsureDirectories();
        using var owner = WorkspaceLease.Acquire(_paths.Workspace);
        using var queueOwner = WorkspaceLease.Acquire(_paths.Testing);
        _queue.RecoverInterrupted();
        _state.SetQueue(_queue.Snapshot());
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var server = new EmbeddedHttpServer(_config.Http, _config.Ui, _paths, _state, _queue, _control, lifetime.Cancel)
        { Reliability = _config.Reliability, Activity = _activity };
        var serverTask = server.RunAsync(lifetime.Token);
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                if (serverTask.IsFaulted) await serverTask;
                if (_queue.GetTestingJobs().Count > 0)
                {
                    _state.SetPhase("interrupted");
                    if (once) break;
                    await Task.Delay(_config.Queue.ScanIntervalMs, lifetime.Token);
                    continue;
                }
                var package = _queue.TryClaimNext();
                _state.SetQueue(_queue.Snapshot());
                if (package is null)
                {
                    _state.SetPhase(once ? "finished" : "idle");
                    if (once) break;
                    await Task.Delay(_config.Queue.ScanIntervalMs, lifetime.Token);
                    continue;
                }
                await ExecuteJobAsync(package, serverTask, lifetime.Token);
                _state.SetQueue(_queue.Snapshot());
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { _state.SetPhase("stopped"); }
        catch { _state.SetPhase("faulted"); throw; }
        finally
        {
            lifetime.Cancel();
            _activity.Detach();
            _control.End();
            try { await serverTask; } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            if (cancellationToken.IsCancellationRequested) _state.SetPhase("stopped");
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
        string status = "invalid_job";
        string? detail = null, failureCaptureError = null;
        int? exitCode = null;
        bool started = false, exited = false, componentStuck = false;
        IReadOnlyList<string> gpuProviders = [];
        using var process = new Process();
        using var tasksCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var logsCts = new CancellationTokenSource();
        using var activity = new RunActivity(resultDirectory);
        MetricCollector? collector = null;
        Task? monitor = null, plan = null, watchdog = null, stdout = null, stderr = null;
        FileStream? stdoutFile = null, stderrFile = null;
        var effectiveArguments = new List<string>();
        try
        {
            attempt.Attempt = (AttemptJournal.Read(package)?.Attempt ?? 0) + 1;
            AttemptJournal.Write(package, attempt);
            File.Copy(Path.Combine(package, "job.json"), Path.Combine(resultDirectory, "job.json"));
            _state.SetPhase("preflight");
            job = JobDefinition.LoadPackage(package);
            preflight = await Preflight.CheckAsync(job, package, _paths.Results, _config.Reliability.Preflight, ct);
            if (job.Plan.Count > 0 && !_config.XemuControl.Enabled)
                preflight = new(false, preflight.ExecutableSha256, [.. preflight.Checks, new("control", false, "Plan actions require XemuControl.")]);
            if (_config.XemuControl.Enabled && job.Arguments.Any(a => a.Equals("-qmp", StringComparison.OrdinalIgnoreCase) || a.StartsWith("-qmp=", StringComparison.OrdinalIgnoreCase)))
                preflight = new(false, preflight.ExecutableSha256, [.. preflight.Checks, new("qmp", false, "The runner owns -qmp.")]);
            AtomicJson.Write(Path.Combine(resultDirectory, "preflight.json"), preflight);
            if (!preflight.Passed) { status = "preflight_failed"; detail = "See preflight.json."; }
            else
            {
                var qmpPort = _config.XemuControl.Enabled ? _control.AllocateQmpPort() : 0;
                effectiveArguments.AddRange(job.Arguments);
                if (_config.XemuControl.Enabled) effectiveArguments.AddRange(["-qmp", $"tcp:{_config.XemuControl.QmpHost}:{qmpPort},server=on,wait=off"]);
                process.StartInfo = new()
                {
                    FileName = JobDefinition.ResolveInsidePackage(package, job.Executable),
                    WorkingDirectory = JobDefinition.ResolveInsidePackage(package, job.WorkingDirectory ?? "."),
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
                };
                foreach (var argument in effectiveArguments) process.StartInfo.ArgumentList.Add(argument);
                foreach (var entry in job.Environment) process.StartInfo.Environment[entry.Key] = entry.Value;
                AtomicJson.Write(Path.Combine(resultDirectory, "launch.json"), new
                {
                    runId, attempt = attempt.Attempt, job = job.Id, executable = job.Executable,
                    executableSha256 = preflight.ExecutableSha256,
                    jobSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(resultDirectory, "job.json")))).ToLowerInvariant(),
                    arguments = effectiveArguments, workingDirectory = job.WorkingDirectory ?? ".", host = HostInfo()
                });
                stdoutFile = OpenLog(Path.Combine(resultDirectory, "stdout.log"));
                stderrFile = OpenLog(Path.Combine(resultDirectory, "stderr.log"));
                attempt.Phase = "starting";
                AttemptJournal.Write(package, attempt);
                status = "start_failed";
                started = process.Start();
                if (!started) throw new InvalidOperationException("Process.Start returned false.");
                stdout = PumpLogAsync(process.StandardOutput.BaseStream, stdoutFile, logsCts.Token);
                stderr = PumpLogAsync(process.StandardError.BaseStream, stderrFile, logsCts.Token);
                attempt.ProcessId = process.Id;
                try { attempt.ProcessStartedUtc = process.StartTime.ToUniversalTime(); }
                catch (InvalidOperationException) when (HasExited(process)) { }
                attempt.Phase = HasExited(process) ? "exited" : "running";
                AttemptJournal.Write(package, attempt);
                _state.BeginJob(job.Id, runId, process.Id);
                _activity.Attach(runId, activity);
                if (_config.XemuControl.Enabled) _control.Begin(process, resultDirectory, qmpPort);
                if (_config.Monitoring.Enabled)
                {
                    collector = new MetricCollector(_config.Monitoring, _state.SetLatestMetric);
                    gpuProviders = collector.GpuProviders.ToArray();
                    monitor = collector.RunAsync(process, Path.Combine(resultDirectory, "metrics.csv"), tasksCts.Token);
                }
                if (job.Plan.Count > 0) plan = _control.ExecutePlanAsync(job.Plan, tasksCts.Token);
                Task<WatchdogTrip>? watch = null;
                if (_config.Reliability.Watchdog.Enabled && _config.XemuControl.Enabled)
                {
                    watch = new ResponsivenessWatchdog(_config.Reliability.Watchdog).RunAsync(
                        async token => { _ = await _control.QueryStatusAsync(token); }, tasksCts.Token);
                    watchdog = watch;
                }
                var exitTask = process.WaitForExitAsync();
                var cancelTask = Task.Delay(Timeout.Infinite, ct);
                var timeoutTask = job.TimeoutSeconds > 0 ? _control.DelayTestTimeAsync(checked(job.TimeoutSeconds * 1000), tasksCts.Token)
                    : Task.Delay(Timeout.Infinite, tasksCts.Token);
                var waiting = new List<Task> { exitTask, cancelTask, timeoutTask };
                if (_config.Http.Enabled) waiting.Add(serverTask);
                if (plan is not null) waiting.Add(plan);
                if (monitor is not null) waiting.Add(monitor);
                if (watch is not null) waiting.Add(watch);
                while (true)
                {
                    var winner = await Task.WhenAny(waiting);
                    if (ct.IsCancellationRequested) { status = "cancelled"; break; }
                    if (exitTask.IsCompleted)
                    {
                        await exitTask; exited = true; exitCode = process.ExitCode;
                        status = exitCode != 0 ? "failed" : plan is not null && !plan.IsCompletedSuccessfully ? "incomplete_plan" : "completed";
                        break;
                    }
                    if (winner == plan)
                    {
                        try { await plan!; } catch (Exception e) { status = "plan_failed"; detail = e.ToString(); break; }
                        waiting.Remove(winner); continue;
                    }
                    if (winner == timeoutTask) { status = "timeout"; detail = $"Exceeded {job.TimeoutSeconds} unpaused seconds."; break; }
                    if (winner == watch)
                    {
                        watchdogTrip = await watch!; status = "unresponsive"; detail = "QMP watchdog threshold reached. This is not a guest-progress verdict."; break;
                    }
                    // Loss of the monitor or HTTP listener is a runner failure, not a passing test.
                    await winner;
                    throw new IOException("A required runner component stopped unexpectedly.");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { status = "cancelled"; }
        catch (Exception e)
        {
            if (started) status = "runner_error";
            detail = e.ToString();
        }
        finally
        {
            tasksCts.Cancel();
            if (started)
            {
                exited = HasExited(process);
                if (!exited && _config.XemuControl.Enabled && status is not ("cancelled" or "unresponsive"))
                {
                    using var screenshotDeadline = new CancellationTokenSource(_config.XemuControl.ScreenshotTimeoutMs);
                    try { _ = await _control.CaptureScreenshotAsync("failure", screenshotDeadline.Token, record: false); }
                    catch (Exception e) { failureCaptureError = e.Message; }
                }
                if (!exited) exited = await StopProcessAsync(process);
                if (exited) { exitCode = process.ExitCode; attempt.Phase = "exited"; AttemptJournal.Write(package, attempt); }
                else { status = "cleanup_failed"; detail = "Target process could not be confirmed stopped. Package retained in Testing."; }
            }
            await SettleAsync(plan, "plan");
            await SettleAsync(watchdog, "watchdog");
            _activity.Detach();
            _control.End();
            await SettleAsync(monitor, "monitor");
            // Child processes can inherit pipes. Never wait forever for EOF.
            try { await Task.WhenAll(stdout ?? Task.CompletedTask, stderr ?? Task.CompletedTask).WaitAsync(TimeSpan.FromMilliseconds(_config.Reliability.ProcessExitTimeoutMs)); }
            catch (Exception e) { logsCts.Cancel(); detail = (detail ?? "") + " Log drain: " + e.Message; }
            await SettleAsync(stdout, "stdout"); await SettleAsync(stderr, "stderr");
            collector?.Dispose();
            if (stdoutFile is not null) await stdoutFile.DisposeAsync();
            if (stderrFile is not null) await stderrFile.DisposeAsync();
        }
        var quality = activity.Snapshot();
        activity.Dispose();
        AtomicJson.Write(Path.Combine(resultDirectory, "result.json"), new
        {
            runId, job = job?.Id ?? Path.GetFileName(package), jobPackage = Path.GetFileName(package),
            attempt = attempt.Attempt, status, detail, startedUtc, endedUtc = DateTimeOffset.UtcNow,
            durationMs = (DateTimeOffset.UtcNow - startedUtc).TotalMilliseconds, processId = attempt.ProcessId, exitCode,
            executable = job?.Executable, executableSha256 = preflight?.ExecutableSha256, arguments = effectiveArguments,
            watchdog = watchdogTrip, failureCaptureError, operatorActivity = quality,
            comparisonStatus = quality.Intervened ? "operator_intervened" : "not_evaluated",
            host = HostInfo(), monitoring = collector is null ? null : new
            { intervalMs = _config.Monitoring.IntervalMs, samples = collector.SampleCount, overruns = collector.OverrunCount,
                droppedWriteSamples = collector.DroppedWriteSamples, gpuProviders }
        });
        if ((started && !exited) || componentStuck) throw new IOException(detail);
        attempt.Phase = "finalized"; AttemptJournal.Write(package, attempt);
        _queue.Complete(package);
        _state.EndJob(status, job?.Id ?? Path.GetFileName(package));

        async Task SettleAsync(Task? task, string component)
        {
            if (task is null) return;
            try { await task.WaitAsync(TimeSpan.FromMilliseconds(_config.Reliability.ProcessExitTimeoutMs)); }
            catch (OperationCanceledException) { }
            catch (TimeoutException e) { componentStuck = true; status = "cleanup_failed"; detail = (detail ?? "") + $" {component} did not stop: {e.Message}"; }
            catch (Exception e) { detail = (detail ?? "") + $" {component}: {e.Message}"; if (status == "completed") status = "runner_error"; }
        }
    }
    private async Task<bool> StopProcessAsync(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(_config.Reliability.ProcessExitTimeoutMs)); }
        catch (Exception e) when (e is TimeoutException or InvalidOperationException) { }
        return HasExited(process);
    }
    private static bool HasExited(Process process) { try { return process.HasExited; } catch (InvalidOperationException) { return false; } }
    private static FileStream OpenLog(string path) => new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
        65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
    private static async Task PumpLogAsync(Stream source, FileStream destination, CancellationToken ct)
    {
        var buffer = new byte[65536];
        while (true)
        {
            var n = await source.ReadAsync(buffer, ct);
            if (n == 0) break;
            await destination.WriteAsync(buffer.AsMemory(0, n), ct);
            await destination.FlushAsync(ct);
        }
    }
    private static object HostInfo() => new
    {
        machine = Environment.MachineName, os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(), processorCount = Environment.ProcessorCount,
        dotnet = Environment.Version.ToString()
    };
}
