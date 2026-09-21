using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using XemuTestRunner.Config;
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

    public RunnerEngine(RunnerConfig config, RunnerPaths paths)
    {
        _config = config;
        _paths = paths;
        _queue = new JobQueue(config, paths);
    }

    public async Task RunAsync(bool once, CancellationToken cancellationToken)
    {
        _queue.EnsureDirectories();
        _queue.RecoverInterrupted();
        _state.SetQueue(_queue.Snapshot());

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var server = new EmbeddedHttpServer(_config.Http, _paths, _state, _queue, lifetime.Cancel);
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
                    _state.SetPhase("idle");
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
            lifetime.Cancel();
            try { await serverTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
            _state.SetPhase("stopped");
        }
    }

    private async Task ExecuteJobAsync(string testingPath, CancellationToken cancellationToken)
    {
        var startedUtc = DateTimeOffset.UtcNow;
        JobDefinition job;
        try
        {
            job = JobDefinition.Load(testingPath);
        }
        catch (Exception ex)
        {
            await FinishWithoutProcessAsync(testingPath, Path.GetFileNameWithoutExtension(testingPath), "invalid_job", ex.Message, startedUtc).ConfigureAwait(false);
            return;
        }

        var runId = $"{startedUtc:yyyyMMdd-HHmmssfff}-{Sanitize(job.Id)}";
        var resultDirectory = Path.Combine(_paths.Results, runId);
        Directory.CreateDirectory(resultDirectory);
        File.Copy(testingPath, Path.Combine(resultDirectory, "job.json"), overwrite: true);

        var executable = ResolveWorkspacePath(job.Executable);
        if (!File.Exists(executable))
        {
            await FinishWithoutProcessAsync(testingPath, job.Id, "start_failed", $"Executable not found: {executable}", startedUtc, resultDirectory, runId).ConfigureAwait(false);
            return;
        }

        var workingDirectory = string.IsNullOrWhiteSpace(job.WorkingDirectory)
            ? Path.GetDirectoryName(executable)!
            : ResolveWorkspacePath(job.WorkingDirectory);

        var executableSha256 = await ComputeSha256Async(executable, cancellationToken).ConfigureAwait(false);
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
            await FinishWithoutProcessAsync(testingPath, job.Id, "start_failed", ex.Message, startedUtc, resultDirectory, runId, executableSha256).ConfigureAwait(false);
            return;
        }

        _state.BeginJob(job.Id, runId, process.Id);

        await using var stdout = new FileStream(Path.Combine(resultDirectory, "stdout.log"), FileMode.Create, FileAccess.Write, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var stderr = new FileStream(Path.Combine(resultDirectory, "stderr.log"), FileMode.Create, FileAccess.Write, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(stdout, cancellationToken);
        var stderrTask = process.StandardError.BaseStream.CopyToAsync(stderr, cancellationToken);

        using var monitoringCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        MetricCollector? collector = null;
        Task? monitorTask = null;
        if (_config.Monitoring.Enabled)
        {
            collector = new MetricCollector(_config.Monitoring, _state.SetLatestMetric);
            monitorTask = collector.RunAsync(process, Path.Combine(resultDirectory, "metrics.csv"), monitoringCts.Token);
        }

        string status;
        string? detail = null;
        int? exitCode = null;

        try
        {
            var exitTask = process.WaitForExitAsync(CancellationToken.None);
            var cancelTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            Task? timeoutTask = job.TimeoutSeconds > 0
                ? Task.Delay(TimeSpan.FromSeconds(job.TimeoutSeconds), CancellationToken.None)
                : null;

            var winner = timeoutTask is null
                ? await Task.WhenAny(exitTask, cancelTask).ConfigureAwait(false)
                : await Task.WhenAny(exitTask, cancelTask, timeoutTask).ConfigureAwait(false);

            if (winner == exitTask)
            {
                await exitTask.ConfigureAwait(false);
                exitCode = process.ExitCode;
                status = exitCode == 0 ? "completed" : "failed";
            }
            else if (timeoutTask is not null && winner == timeoutTask)
            {
                status = "timeout";
                detail = $"Process exceeded TimeoutSeconds={job.TimeoutSeconds}.";
                KillProcessTree(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                exitCode = process.ExitCode;
            }
            else
            {
                status = "cancelled";
                detail = "Runner cancellation requested.";
                KillProcessTree(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                exitCode = process.ExitCode;
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
            monitoringCts.Cancel();
            if (monitorTask is not null)
            {
                try { await monitorTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
            try { await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }

        var endedUtc = DateTimeOffset.UtcNow;
        var result = new
        {
            runId,
            job = job.Id,
            status,
            detail,
            startedUtc,
            endedUtc,
            durationMs = (endedUtc - startedUtc).TotalMilliseconds,
            processId = process.Id,
            exitCode,
            executable,
            executableSha256,
            arguments = job.Arguments,
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
        _queue.Complete(testingPath);
        _state.EndJob();
    }

    private async Task FinishWithoutProcessAsync(
        string testingPath,
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
        if (!File.Exists(Path.Combine(resultDirectory, "job.json")))
            File.Copy(testingPath, Path.Combine(resultDirectory, "job.json"), overwrite: true);

        var endedUtc = DateTimeOffset.UtcNow;
        var result = new
        {
            runId,
            job = jobId,
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

        _queue.Complete(testingPath);
        _state.EndJob();
        _state.SetQueue(_queue.Snapshot());
    }

    private string ResolveWorkspacePath(string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_paths.Workspace, path));

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
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
