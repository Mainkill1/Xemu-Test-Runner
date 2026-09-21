using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Control;
using XemuTestRunner.Monitoring;
using XemuTestRunner.Networking;
using XemuTestRunner.Queue;

namespace XemuTestRunner.Runtime;

public sealed class RunnerEngine
{
    private readonly RunnerConfig _config;
    private readonly RunnerPaths _paths;
    private readonly RunnerState _state = new();
    private readonly JobQueue _queue;
    private readonly XemuControlManager _control;

    public RunnerState State => _state;

    public RunnerEngine(RunnerConfig config, RunnerPaths paths)
    {
        _config = config;
        _paths = paths;
        _queue = new JobQueue(config, paths);
        _control = new XemuControlManager(config.XemuControl);
    }

    public async Task RunAsync(bool once, CancellationToken cancellationToken)
    {
        _queue.EnsureDirectories();
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
            lifetime.Cancel);
        var serverTask = server.RunAsync(lifetime.Token);

        try
        {
            _state.SetPhase("idle");

            while (!lifetime.IsCancellationRequested)
            {
                if (serverTask.IsFaulted)
                    await serverTask.ConfigureAwait(false);

                var testing = _queue.GetTestingJobs();
                if (testing.Count > 0)
                {
                    _state.SetPhase("interrupted");
                    _state.SetQueue(_queue.Snapshot());
                    if (once)
                        break;
                    await Task.Delay(_config.Queue.ScanIntervalMs, lifetime.Token).ConfigureAwait(false);
                    continue;
                }

                var claimed = _queue.TryClaimNext();
                _state.SetQueue(_queue.Snapshot());
                if (claimed is null)
                {
                    _state.SetPhase(once ? "finished" : "idle");
                    if (once)
                        break;
                    await Task.Delay(_config.Queue.ScanIntervalMs, lifetime.Token).ConfigureAwait(false);
                    continue;
                }

                await ExecuteJobAsync(claimed, lifetime.Token).ConfigureAwait(false);
                _state.SetQueue(_queue.Snapshot());
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _control.End();
            lifetime.Cancel();
            try { await serverTask.ConfigureAwait(false); } catch (OperationCanceledException) { }

            if (cancellationToken.IsCancellationRequested)
                _state.SetPhase("stopped");
        }
    }

    private async Task ExecuteJobAsync(string testingPackage, CancellationToken cancellationToken)
    {
        var startedUtc = DateTimeOffset.UtcNow;
        JobDefinition job;

        try
        {
            job = JobDefinition.LoadPackage(testingPackage);
        }
        catch (Exception ex)
        {
            await FinishWithoutProcessAsync(
                testingPackage,
                Path.GetFileName(testingPackage),
                "invalid_job",
                ex.Message,
                startedUtc).ConfigureAwait(false);
            return;
        }

        var runId = $"{startedUtc:yyyyMMdd-HHmmssfff}-{Sanitize(job.Id)}";
        var resultDirectory = Path.Combine(_paths.Results, runId);
        Directory.CreateDirectory(resultDirectory);
        File.Copy(Path.Combine(testingPackage, "job.json"), Path.Combine(resultDirectory, "job.json"), overwrite: true);

        var executable = JobDefinition.ResolveInsidePackage(testingPackage, job.Executable);
        var workingDirectory = string.IsNullOrWhiteSpace(job.WorkingDirectory)
            ? testingPackage
            : JobDefinition.ResolveInsidePackage(testingPackage, job.WorkingDirectory);

        if (!Directory.Exists(workingDirectory))
        {
            await FinishWithoutProcessAsync(
                testingPackage,
                job.Id,
                "start_failed",
                $"Working directory does not exist: {workingDirectory}",
                startedUtc,
                resultDirectory,
                runId).ConfigureAwait(false);
            return;
        }

        if (_config.XemuControl.Enabled &&
            job.Arguments.Any(argument => string.Equals(argument, "-qmp", StringComparison.OrdinalIgnoreCase)))
        {
            await FinishWithoutProcessAsync(
                testingPackage,
                job.Id,
                "invalid_job",
                "Do not supply -qmp in the job plan while XemuControl is enabled; the runner owns the QMP endpoint.",
                startedUtc,
                resultDirectory,
                runId).ConfigureAwait(false);
            return;
        }

        var executableSha256 = await ComputeSha256Async(executable, cancellationToken).ConfigureAwait(false);
        var qmpPort = _config.XemuControl.Enabled ? _control.AllocateQmpPort() : 0;

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = false
        };

        foreach (var argument in job.Arguments)
            startInfo.ArgumentList.Add(argument);

        if (_config.XemuControl.Enabled)
        {
            startInfo.ArgumentList.Add("-qmp");
            startInfo.ArgumentList.Add($"tcp:{_config.XemuControl.QmpHost}:{qmpPort},server=on,wait=off");
        }

        foreach (var variable in job.Environment)
            startInfo.Environment[variable.Key] = variable.Value;

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Process.Start returned false.");
        }
        catch (Exception ex)
        {
            await FinishWithoutProcessAsync(
                testingPackage,
                job.Id,
                "start_failed",
                ex.Message,
                startedUtc,
                resultDirectory,
                runId,
                executableSha256).ConfigureAwait(false);
            return;
        }

        _state.BeginJob(job.Id, runId, process.Id);

        if (_config.XemuControl.Enabled)
            _control.Begin(process, resultDirectory, qmpPort);

        await using var stdout = new FileStream(
            Path.Combine(resultDirectory, "stdout.log"),
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            256 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        await using var stderr = new FileStream(
            Path.Combine(resultDirectory, "stderr.log"),
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            256 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(stdout, cancellationToken);
        var stderrTask = process.StandardError.BaseStream.CopyToAsync(stderr, cancellationToken);

        using var monitoringCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        MetricCollector? collector = null;
        Task? monitorTask = null;

        if (_config.Monitoring.Enabled)
        {
            collector = new MetricCollector(_config.Monitoring, _state.SetLatestMetric);
            monitorTask = collector.RunAsync(
                process,
                Path.Combine(resultDirectory, "metrics.csv"),
                monitoringCts.Token);
        }

        using var planCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var timeoutCts = new CancellationTokenSource();
        Task? planTask = null;

        if (job.Plan.Count > 0)
        {
            planTask = _config.XemuControl.Enabled
                ? _control.ExecutePlanAsync(job.Plan, planCts.Token)
                : Task.FromException(new InvalidOperationException("Job contains control plan steps but XemuControl is disabled."));
        }

        string status;
        string? detail = null;
        int? exitCode = null;

        try
        {
            var exitTask = process.WaitForExitAsync(CancellationToken.None);
            var cancelTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var timeoutTask = job.TimeoutSeconds > 0
                ? _control.DelayTestTimeAsync(
                    checked(job.TimeoutSeconds * 1000),
                    timeoutCts.Token)
                : Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token);

            Task planWatch = planTask ?? Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);

            while (true)
            {
                var winner = await Task.WhenAny(exitTask, cancelTask, timeoutTask, planWatch).ConfigureAwait(false);

                if (winner == planWatch)
                {
                    try
                    {
                        await planWatch.ConfigureAwait(false);
                        planWatch = Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
                        continue;
                    }
                    catch (Exception ex)
                    {
                        status = "plan_failed";
                        detail = ex.ToString();
                        KillProcessTree(process);
                        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                        exitCode = process.ExitCode;
                        break;
                    }
                }

                if (winner == exitTask)
                {
                    await exitTask.ConfigureAwait(false);
                    exitCode = process.ExitCode;
                    status = exitCode == 0 ? "completed" : "failed";
                    break;
                }

                if (winner == timeoutTask)
                {
                    status = "timeout";
                    detail = $"Process exceeded TimeoutSeconds={job.TimeoutSeconds}.";
                    KillProcessTree(process);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    exitCode = process.ExitCode;
                    break;
                }

                status = "cancelled";
                detail = "Runner cancellation requested.";
                KillProcessTree(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                exitCode = process.ExitCode;
                break;
            }
        }
        catch (Exception ex)
        {
            status = "runner_error";
            detail = ex.ToString();
            KillProcessTree(process);
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            try { exitCode = process.ExitCode; } catch { }
        }
        finally
        {
            timeoutCts.Cancel();
            planCts.Cancel();
            if (planTask is not null)
            {
                try { await planTask.ConfigureAwait(false); }
                catch (OperationCanceledException) when (planCts.IsCancellationRequested) { }
                catch { }
            }

            _control.End();

            monitoringCts.Cancel();
            if (monitorTask is not null)
            {
                try { await monitorTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }

            try { await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        var endedUtc = DateTimeOffset.UtcNow;
        var result = new
        {
            runId,
            job = job.Id,
            jobPackage = Path.GetFileName(testingPackage),
            status,
            detail,
            startedUtc,
            endedUtc,
            durationMs = (endedUtc - startedUtc).TotalMilliseconds,
            processId = process.Id,
            exitCode,
            executable = job.Executable,
            executableSha256,
            arguments = job.Arguments,
            qmp = _config.XemuControl.Enabled ? new
            {
                host = _config.XemuControl.QmpHost,
                port = qmpPort
            } : null,
            host = HostInfo(),
            monitoring = collector is null ? null : new
            {
                intervalMs = _config.Monitoring.IntervalMs,
                samples = collector.SampleCount,
                overruns = collector.OverrunCount,
                droppedWriteSamples = collector.DroppedWriteSamples,
                gpuProviders = collector.GpuProviders
            }
        };

        await File.WriteAllTextAsync(
            Path.Combine(resultDirectory, "result.json"),
            JsonSerializer.Serialize(result, ConfigLoader.JsonOptions),
            CancellationToken.None).ConfigureAwait(false);

        collector?.Dispose();
        _queue.Complete(testingPackage);
        _state.EndJob(status, job.Id);
    }

    private async Task FinishWithoutProcessAsync(
        string testingPackage,
        string jobId,
        string status,
        string detail,
        DateTimeOffset startedUtc,
        string? existingResultDirectory = null,
        string? existingRunId = null,
        string? executableSha256 = null)
    {
        var runId = existingRunId ?? $"{startedUtc:yyyyMMdd-HHmmssfff}-{Sanitize(jobId)}";
        var resultDirectory = existingResultDirectory ?? Path.Combine(_paths.Results, runId);
        Directory.CreateDirectory(resultDirectory);

        var sourceJob = Path.Combine(testingPackage, "job.json");
        if (File.Exists(sourceJob) && !File.Exists(Path.Combine(resultDirectory, "job.json")))
            File.Copy(sourceJob, Path.Combine(resultDirectory, "job.json"), overwrite: true);

        var endedUtc = DateTimeOffset.UtcNow;
        var result = new
        {
            runId,
            job = jobId,
            jobPackage = Path.GetFileName(testingPackage),
            status,
            detail,
            startedUtc,
            endedUtc,
            durationMs = (endedUtc - startedUtc).TotalMilliseconds,
            executableSha256,
            host = HostInfo()
        };

        await File.WriteAllTextAsync(
            Path.Combine(resultDirectory, "result.json"),
            JsonSerializer.Serialize(result, ConfigLoader.JsonOptions)).ConfigureAwait(false);

        _queue.Complete(testingPackage);
        _state.EndJob(status, jobId);
        _state.SetQueue(_queue.Snapshot());
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static object HostInfo() => new
    {
        machine = Environment.MachineName,
        os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
        processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        processorCount = Environment.ProcessorCount,
        dotnet = Environment.Version.ToString()
    };

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = string.Concat(value.Select(ch => invalid.Contains(ch) || char.IsWhiteSpace(ch) ? '-' : ch));
        return cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }
}
