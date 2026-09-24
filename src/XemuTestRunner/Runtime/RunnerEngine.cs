using System.Diagnostics;
using System.Security.Cryptography;
using XemuTestRunner.Config;
using XemuTestRunner.Control;
using XemuTestRunner.Diagnostics;
using XemuTestRunner.Monitoring;
using XemuTestRunner.Networking;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;
using XemuTestRunner.Workstation;

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
    private PreservedTarget? _preservedTarget;

    public RunnerState State => _state;

    public RunnerEngine(RunnerConfig config, RunnerPaths paths)
    {
        _config = config;
        _paths = paths;
        _queue = new(config, paths);
        _control = new(config.XemuControl);
        _diagnostics = new(config.Diagnostics, _control, _state, _activity);
    }

    public Task RunAsync(
        bool once,
        CancellationToken cancellationToken) =>
        RunAsync(
            once,
            maxJobs: null,
            cancellationToken);

    public async Task RunAsync(
        bool once,
        int? maxJobs,
        CancellationToken cancellationToken)
    {
        if (maxJobs is <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(maxJobs),
                "maxJobs must be greater than zero when supplied.");
        _queue.EnsureDirectories();
        using var owner = WorkspaceLease.Acquire(_paths.Workspace);
        using var queueOwner = WorkspaceLease.Acquire(_paths.Testing);
        RuntimeStateManager.RecoverAuthorizedCleanups(_paths.Workspace, _paths.Results);
        _queue.RecoverInterrupted();
        _state.SetQueue(_queue.Snapshot());

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        MetricCollector? telemetry = null;
        Task? telemetryTask = null;
        if (_config.Monitoring.Enabled)
        {
            telemetry = new MetricCollector(
                _config.Monitoring,
                _state.SetLatestMetric);
            telemetryTask = telemetry.RunAsync(lifetime.Token);
        }

        long lastWorkstationEvent = 0;
        using var workstationMonitor = new WorkstationStateMonitor(
            workstation =>
            {
                _state.SetWorkstationState(workstation);

                if (workstation.EventSequence > 0 &&
                    workstation.EventSequence !=
                        Interlocked.Read(ref lastWorkstationEvent))
                {
                    Interlocked.Exchange(
                        ref lastWorkstationEvent,
                        workstation.EventSequence);

                    if (_state.HasActiveJob)
                        _activity.Mark("host_state", workstation);
                }
            });
        workstationMonitor.Start();

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
            Diagnostics = _diagnostics,
            CrashDiagnostics = _config.Diagnostics
        };
        var serverTask = server.RunAsync(lifetime.Token);

        var jobsFinished = 0;
        DateTimeOffset? finiteWaitStarted = null;

        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await ReapPreservedTargetAsync().ConfigureAwait(false);

                if (serverTask.IsFaulted)
                    await serverTask.ConfigureAwait(false);
                if (telemetryTask?.IsFaulted == true)
                    await telemetryTask.ConfigureAwait(false);

                var testingJobs =
                    _queue.GetTestingJobs();

                if (testingJobs.Count > 0)
                {
                    var queueIssue =
                        _state.Snapshot().QueueIssue;

                    if (queueIssue is null)
                    {
                        queueIssue = new QueueIssue(
                            "testing_occupied",
                            Path.GetFileName(testingJobs[0]),
                            "Testing contains a package owned by a previous or preserved attempt. Inspect the package/process state before requeueing it.",
                            DateTimeOffset.UtcNow,
                            Retryable: false,
                            HoldsTesting: true);
                        _state.SetQueueIssue(queueIssue);
                    }

                    _state.SetPhase("queue_blocked");

                    if (once)
                        break;

                    await Task.Delay(
                        _config.Queue.ScanIntervalMs,
                        lifetime.Token).ConfigureAwait(false);
                    continue;
                }

                var claim = _queue.TryClaimNext();
                _state.SetQueue(_queue.Snapshot());

                if (claim.Issue is not null)
                {
                    var issue = claim.Issue;

                    if (once && issue.Retryable)
                    {
                        finiteWaitStarted ??=
                            DateTimeOffset.UtcNow;

                        if (_config.Queue.FiniteWaitTimeoutSeconds > 0 &&
                            DateTimeOffset.UtcNow -
                                finiteWaitStarted.Value >=
                            TimeSpan.FromSeconds(
                                _config.Queue.FiniteWaitTimeoutSeconds))
                        {
                            issue = new QueueIssue(
                                "package_wait_timeout",
                                issue.Package,
                                $"Finite run waited {_config.Queue.FiniteWaitTimeoutSeconds} seconds for a retryable package state. Last issue: {issue.Code}: {issue.Message}",
                                DateTimeOffset.UtcNow,
                                Retryable: false,
                                HoldsTesting: false);
                        }
                    }
                    else
                    {
                        finiteWaitStarted = null;
                    }

                    _state.SetQueueIssue(issue);
                    _state.SetPhase(
                        issue.Retryable
                            ? "waiting_for_package"
                            : "queue_blocked");

                    if (once && !issue.Retryable)
                        break;

                    await Task.Delay(
                        _config.Queue.ScanIntervalMs,
                        lifetime.Token).ConfigureAwait(false);
                    continue;
                }

                finiteWaitStarted = null;

                if (claim.Package is null)
                {
                    _state.SetQueueIssue(null);
                    _state.SetPhase(once ? "finished" : "idle");

                    if (once)
                        break;

                    await Task.Delay(
                        _config.Queue.ScanIntervalMs,
                        lifetime.Token).ConfigureAwait(false);
                    continue;
                }

                var claimIssue = _queue.VerifyClaimedPackage(claim.Package);
                if (claimIssue is not null)
                {
                    _state.SetQueueIssue(claimIssue);
                    _state.SetQueue(_queue.Snapshot());
                    _state.SetPhase("queue_blocked");

                    if (once)
                        break;

                    await Task.Delay(
                        _config.Queue.ScanIntervalMs,
                        lifetime.Token).ConfigureAwait(false);
                    continue;
                }

                finiteWaitStarted = null;
                _state.SetQueueIssue(null);
                await ExecuteJobAsync(
                    claim.Package,
                    serverTask,
                    telemetry,
                    lifetime.Token).ConfigureAwait(false);
                jobsFinished++;
                _state.SetQueue(_queue.Snapshot());

                if (maxJobs is not null &&
                    jobsFinished >= maxJobs.Value)
                {
                    _state.SetPhase("finished");
                    break;
                }
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

            await ReleasePreservedTargetHandlesAsync()
                .ConfigureAwait(false);
            _control.End();

            try
            {
                if (telemetryTask is not null)
                    await telemetryTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (lifetime.IsCancellationRequested)
            {
            }
            telemetry?.Dispose();

            try { await serverTask.ConfigureAwait(false); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }

            if (cancellationToken.IsCancellationRequested)
                _state.SetPhase("stopped");
        }
    }

    private async Task ExecuteJobAsync(
        string package,
        Task serverTask,
        MetricCollector? telemetry,
        CancellationToken ct)
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
        bool preserveTarget = false;
        bool runnerTerminated = false;
        CrashCapture? crashCapture = null;
        CrashReport? crashReport = null;
        NativeExitStatus? nativeExit = null;
        IReadOnlyList<string> gpuProviders = [];
        WorkstationStateSnapshot? workstationStart = null;
        RuntimeMaterialization? runtimeState = null;
        InputManifest? inputManifest = null;
        WorkloadEvaluation workloadEvaluation = new(
            CorrectnessOutcome.NotEvaluated,
            EvidenceOutcome.NotEvaluated,
            [],
            []);
        RunAssessment? assessment = null;
        HostInventory? hostInventory = null;

        TargetLaunch? launch = null;
        Process? process = null;
        using var tasksCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var logsCts = new CancellationTokenSource();
        using var activity = new RunActivity(resultDirectory);

        MetricRecordingSummary? metricSummary = null;
        bool metricRecordingStarted = false;
        bool holdPackage = false;
        QueueIssue? packageIssue = null;
        Task? plan = null;
        Task? watchdog = null;
        Task? recordingWriter = null;
        MeasurementTimeline? timeline = null;
        bool planCompleted = false;
        Task? stdout = null;
        Task? stderr = null;
        FileStream? stdoutFile = null;
        FileStream? stderrFile = null;
        var effectiveArguments = new List<string>();
        var launchEnvironment =
            new Dictionary<string, string>(
                StringComparer.Ordinal);

        try
        {
            attempt.Attempt =
                (AttemptJournal.Read(package)?.Attempt ?? 0) + 1;
            AttemptJournal.Write(package, attempt);

            var sourceManifest = Path.Combine(package, "job.json");
            var resultManifest = Path.Combine(resultDirectory, "job.json");
            try
            {
                File.Copy(sourceManifest, resultManifest);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
                throw new PackageMutationException(
                    $"The claimed package changed or became unavailable before its job plan could be frozen for this run: {ex.Message}",
                    ex);
            }

            _state.SetPhase("preflight");
            job = JobDefinition.LoadPackage(package, resultManifest);
            planCompleted = job.Plan.Count == 0;

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

            AtomicJson.Write(
                Path.Combine(resultDirectory, "preflight.json"),
                preflight);

            if (!preflight.Passed)
            {
                var packageChanged = preflight.Checks.Any(check =>
                    !check.Passed &&
                    check.Name is
                        "package" or
                        "executable" or
                        "executable_stable" or
                        "required_file" or
                        "input_file" or
                        "runtime_seed" or
                        "working_directory");

                if (packageChanged)
                {
                    status = "package_changed";
                    detail =
                        "Package integrity changed after claim. See preflight.json. " +
                        "The package is being held in Testing instead of being launched.";
                    holdPackage = true;
                    packageIssue = new QueueIssue(
                        "package_changed_during_preflight",
                        Path.GetFileName(package),
                        detail,
                        DateTimeOffset.UtcNow,
                        Retryable: false,
                        HoldsTesting: true);
                    _state.SetQueueIssue(packageIssue);
                }
                else
                {
                    status = "preflight_failed";
                    detail = "See preflight.json.";
                }
            }
            else
            {
                status = "preflight_failed";

                hostInventory =
                    await HostInventoryCollector.CaptureAsync(
                        _state.Snapshot().LatestMetric,
                        ct).ConfigureAwait(false);
                AtomicJson.Write(
                    Path.Combine(
                        resultDirectory,
                        "host-inventory.json"),
                    hostInventory);
                AtomicJson.Write(
                    Path.Combine(
                        resultDirectory,
                        "pre-run-environment.json"),
                    new
                    {
                        capturedUtc = DateTimeOffset.UtcNow,
                        metric =
                            _state.Snapshot().LatestMetric,
                        workstation =
                            _state.Snapshot().Workstation
                    });

                runtimeState =
                    await RuntimeStateManager.MaterializeAsync(
                        job.RuntimeState,
                        package,
                        _paths.Workspace,
                        runId,
                        ct).ConfigureAwait(false);

                inputManifest =
                    await InputManifestBuilder.BuildAsync(
                        job,
                        package,
                        resultManifest,
                        preflight.ExecutableSha256,
                        runtimeState,
                        ct).ConfigureAwait(false);
                InputManifestBuilder.Write(
                    resultDirectory,
                    inputManifest);

                AtomicJson.Write(
                    Path.Combine(
                        resultDirectory,
                        "runtime-state.json"),
                    new
                    {
                        enabled = runtimeState is not null,
                        directory = runtimeState?.Directory,
                        files = runtimeState?.Files ?? []
                    });

                var qmpPort = _config.XemuControl.Enabled
                    ? _control.AllocateQmpPort()
                    : 0;

                foreach (var argument in job.Arguments)
                {
                    effectiveArguments.Add(
                        RuntimeStateManager.Expand(
                            argument,
                            package,
                            resultDirectory,
                            runId,
                            runtimeState));
                }

                foreach (var variable in job.Environment)
                {
                    launchEnvironment[variable.Key] =
                        RuntimeStateManager.Expand(
                            variable.Value,
                            package,
                            resultDirectory,
                            runId,
                            runtimeState);
                }

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
                            Path.Combine(resultDirectory,
                                "job.json")))).ToLowerInvariant(),
                    arguments = effectiveArguments,
                    environmentKeys = launchEnvironment.Keys.OrderBy(key => key).ToArray(),
                    workingDirectory = job.WorkingDirectory ?? ".",
                    runtimeDirectory = runtimeState?.Directory,
                    experiment = job.Experiment,
                    operations = job.Operations,
                    host = HostInfo()
                });

                attempt.Phase = "starting";
                AttemptJournal.Write(package, attempt);
                status = "start_failed";

                var finalClaimIssue =
                    _queue.VerifyClaimedPackage(package);
                if (finalClaimIssue is not null)
                {
                    throw new PackageMutationException(
                        finalClaimIssue.Message,
                        new IOException(finalClaimIssue.Code));
                }

                crashCapture = new CrashCapture(_config.Diagnostics, runId, package, executable,
                    preflight.ExecutableSha256, resultDirectory, DateTimeOffset.UtcNow);
                launch = await TargetLaunch.StartAsync(
                    _config.Diagnostics,
                    job,
                    executable,
                    workingDirectory,
                    effectiveArguments,
                    launchEnvironment,
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
                        "RenderDoc bridge diagnostics are stored under diagnostics/_renderdoc-session/.",
                        ct).ConfigureAwait(false);
                }

                attempt.ProcessId = process.Id;
                try { attempt.ProcessStartedUtc = process.StartTime.ToUniversalTime(); }
                catch (InvalidOperationException) when (HasExited(process)) { }
                attempt.Phase = HasExited(process) ? "exited" : "running";
                AttemptJournal.Write(package, attempt);

                _state.BeginJob(
                    job.Id,
                    runId,
                    process.Id,
                    job.Operations);
                _activity.Attach(runId, activity);

                workstationStart = _state.Snapshot().Workstation;
                if (workstationStart.RenderingRisk)
                {
                    _activity.Mark("host_state", new
                    {
                        initial = true,
                        workstation = workstationStart
                    });
                }

                // The lifetime collector already samples host state. Attaching
                // the xemu process also enables per-run CSV recording; no second
                // polling loop is created.
                if (job.Plan.Any(step =>
                        step.Type.Equals(
                            "segment_start",
                            StringComparison.OrdinalIgnoreCase)))
                {
                    timeline =
                        new MeasurementTimeline(resultDirectory);
                }

                if (telemetry is not null)
                {
                    gpuProviders =
                        telemetry.GpuProviders.ToArray();
                    telemetry.StartRecording(
                        process,
                        Path.Combine(
                            resultDirectory,
                            "metrics.csv"));
                    metricRecordingStarted = true;
                    recordingWriter =
                        telemetry.RecordingTask;
                }

                if (_config.XemuControl.Enabled)
                {
                    try
                    {
                        _control.Begin(
                            process,
                            resultDirectory,
                            qmpPort,
                            job.Workload.GuestProgressPath);
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
                            _ = await _diagnostics
                                .RunByIdAsync(id, token)
                                .ConfigureAwait(false);
                        },
                        segmentStart: name =>
                        {
                            timeline?.Start(name);
                            telemetry?.SetSegment(name);
                        },
                        segmentEnd: name =>
                        {
                            timeline?.End(name);
                            telemetry?.SetSegment(null);
                        },
                        artifactWaiter: async (
                            condition,
                            timeoutMs,
                            pollIntervalMs,
                            token) =>
                        {
                            await ArtifactConditionWaiter.WaitAsync(
                                condition,
                                timeoutMs,
                                pollIntervalMs,
                                package,
                                resultDirectory,
                                runtimeState,
                                token).ConfigureAwait(false);
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

                var exitTask = launch.WaitForExitAsync();
                var cancelTask = Task.Delay(Timeout.Infinite, ct);
                var timeoutTask = job.TimeoutSeconds > 0
                    ? _control.DelayTestTimeAsync(checked(job.TimeoutSeconds * 1000), tasksCts.Token)
                    : Task.Delay(Timeout.Infinite, tasksCts.Token);

                var waiting = new List<Task> { exitTask, cancelTask, timeoutTask };
                if (_config.Http.Enabled)
                    waiting.Add(serverTask);
                if (plan is not null)
                    waiting.Add(plan);
                if (watch is not null)
                    waiting.Add(watch);
                if (recordingWriter is not null)
                    waiting.Add(recordingWriter);

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
                        exitCode = launch?.ExitCode ?? process.ExitCode;
                        status = exitCode != 0
                            ? "failed"
                            : plan is not null && !plan.IsCompletedSuccessfully && !_control.QuitRequested
                                ? "incomplete_plan"
                                : "completed";
                        break;
                    }

                    if (winner == plan)
                    {
                        try
                        {
                            await plan!.ConfigureAwait(false);
                            planCompleted = true;
                        }
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

                    if (winner == recordingWriter)
                    {
                        try
                        {
                            await recordingWriter!
                                .ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            status = "evidence_failure";
                            detail =
                                "Metric evidence writer failed during the active workload: " +
                                ex;
                            break;
                        }

                        status = "evidence_failure";
                        detail =
                            "Metric evidence writer stopped before the test ended.";
                        break;
                    }

                    await winner.ConfigureAwait(false);
                    throw new IOException(
                        "A required runner component stopped unexpectedly.");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status = "cancelled";
        }
        catch (PackageMutationException ex)
        {
            status = "package_changed";
            detail = ex.Message;
            holdPackage = true;
            packageIssue = new QueueIssue(
                "package_changed_during_preflight",
                Path.GetFileName(package),
                detail,
                DateTimeOffset.UtcNow,
                Retryable: false,
                HoldsTesting: true);
            _state.SetQueueIssue(packageIssue);
        }
        catch (Exception ex)
        {
            if (started && status is ("start_failed" or "invalid_job"))
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
                // The supervisor's validated native receipt and a previously
                // awaited exit cannot be undone by a stale target Process handle.
                exited = ConfirmTargetExit(exited, HasExited(process), launch?.NativeExit is not null);
                preserveTarget =
                    !exited &&
                    _config.Reliability.PreserveTargetOnRunnerError &&
                    status is ("runner_error" or "control_error");

                if (ShouldCaptureFailure(status, preserveTarget, exited))
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

                // Automatic diagnostics can outlive the target. Check again
                // before an external provider could photograph another window.
                exited = ConfirmTargetExit(exited, HasExited(process), launch?.NativeExit is not null);
                if (ShouldCaptureFailure(status, preserveTarget, exited) &&
                    _config.XemuControl.Enabled &&
                    automaticBundle is null)
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

                exited = ConfirmTargetExit(exited, HasExited(process), launch?.NativeExit is not null);
                if (!preserveTarget && !exited)
                {
                    var stopped = await StopProcessAsync(process).ConfigureAwait(false);
                    runnerTerminated = stopped.TerminationRequested;
                    exited = ConfirmTargetExit(exited, stopped.Exited, launch?.NativeExit is not null);
                }

                if (preserveTarget)
                {
                    detail = (detail ?? status) +
                        " Target intentionally left running because Reliability.PreserveTargetOnRunnerError is enabled. " +
                        "The package remains in Testing for inspection; stop xemu manually before retrying.";
                }
                else if (exited)
                {
                    // A native-exit supervisor may still be publishing its tiny
                    // receipt after the target died during control initialization.
                    if (launch is not null)
                    {
                        try { await launch.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(_config.Reliability.ProcessExitTimeoutMs)).ConfigureAwait(false); }
                        catch (TimeoutException ex) { detail = (detail ?? "") + " Exit receipt: " + ex.Message; }
                        nativeExit = launch.NativeExit;
                    }
                    try { exitCode = launch?.ExitCode ?? process.ExitCode; }
                    catch (InvalidOperationException) { detail = (detail ?? "") + " Native exit receipt unavailable."; }
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
            if (plan is null ||
                plan.IsCompletedSuccessfully ||
                _control.QuitRequested)
            {
                planCompleted = true;
            }

            await SettleAsync(watchdog, "watchdog").ConfigureAwait(false);

            telemetry?.SetSegment(null);
            timeline?.Dispose();

            _diagnostics.Detach();
            _activity.Detach();

            if (!preserveTarget)
                _control.End();

            if (metricRecordingStarted && telemetry is not null)
            {
                try
                {
                    metricSummary =
                        await telemetry.StopRecordingAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (status == "completed")
                        status = "evidence_failure";

                    detail = (detail ?? "") +
                        " Metric recording finalization failed: " +
                        ex.Message;
                }
            }

            if (!preserveTarget)
            {
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
                        detail = (detail ?? "") +
                            " Log drain: " + ex.Message;
                    }

                    await SettleAsync(
                        stdout,
                        "stdout").ConfigureAwait(false);
                    await SettleAsync(
                        stderr,
                        "stderr").ConfigureAwait(false);
                }

                if (stdoutFile is not null)
                    await stdoutFile.DisposeAsync().ConfigureAwait(false);
                if (stderrFile is not null)
                    await stderrFile.DisposeAsync().ConfigureAwait(false);
                if (launch is not null)
                    await launch.DisposeAsync().ConfigureAwait(false);
            }
        }

        var quality = activity.Snapshot();
        activity.Dispose();

        if (job is not null)
        {
            try
            {
                workloadEvaluation =
                    await WorkloadEvaluator.EvaluateAsync(
                        job,
                        package,
                        resultDirectory,
                        runtimeState,
                        checked((int)Math.Min(
                            int.MaxValue,
                            metricSummary?.Samples ?? 0)),
                        planCompleted,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                workloadEvaluation = new WorkloadEvaluation(
                    job.Workload.CorrectnessChecks.Count > 0
                        ? CorrectnessOutcome.Incomplete
                        : CorrectnessOutcome.NotEvaluated,
                    EvidenceOutcome.Invalid,
                    [
                        new AssessmentCheck(
                            "workload_evaluation",
                            false,
                            "evidence",
                            ex.ToString())
                    ],
                    []);
            }
        }

        if (crashCapture is not null)
        {
            _state.SetPhase("collecting_diagnostics");
            crashReport = await crashCapture.CollectAsync(attempt.ProcessId, exitCode, nativeExit,
                runnerTerminated, exited, status).ConfigureAwait(false);
            if (crashReport.Crashed && status != "cancelled" &&
                (!runnerTerminated || crashReport.Source is "windowsApplicationError" or "systemd-coredump"))
                status = "crashed";
        }

        assessment = RunAssessmentEvaluator.Evaluate(
            status,
            workloadEvaluation,
            quality,
            job);

        AtomicJson.Write(
            Path.Combine(resultDirectory, "assessment.json"),
            assessment);

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
            crash = crashReport,
            diagnosticBundle = "diagnostic-bundle.json",
            launchMode = job?.LaunchMode,
            snapshotName = job?.SnapshotName,
            startPaused = job?.StartPaused,
            executable = job?.Executable,
            executableSha256 = preflight?.ExecutableSha256,
            arguments = effectiveArguments,
            watchdog = watchdogTrip,
            failureCaptureError,
            automaticDiagnostic = automaticBundle,
            automaticDiagnosticError = automaticBundleError,
            diagnostics = _diagnostics.Snapshot().Completed,
            operatorActivity = quality,
            workload = workloadEvaluation,
            assessment,
            correctnessStatus =
                assessment.Correctness.ToString().ToLowerInvariant(),
            evidenceStatus =
                assessment.Evidence.ToString().ToLowerInvariant(),
            comparisonStatus =
                assessment.Comparison.ToString().ToLowerInvariant(),
            experiment = job?.Experiment,
            operations = job?.Operations,
            inputManifest = inputManifest is null
                ? null
                : "input-manifest.json",
            runtimeDirectory = runtimeState?.Directory,
            hostInventory =
                hostInventory is null
                    ? null
                    : "host-inventory.json",
            workstationStart,
            workstationEnd = _state.Snapshot().Workstation,
            host = HostInfo(),
            monitoring = metricSummary is null ? null : new
            {
                intervalMs = _config.Monitoring.IntervalMs,
                samples = metricSummary.Samples,
                overruns = metricSummary.Overruns,
                droppedWriteSamples = metricSummary.DroppedWriteSamples,
                averageCollectorDutyPercent =
                    metricSummary.AverageCollectorDutyPercent,
                maxCollectorDutyPercent =
                    metricSummary.MaxCollectorDutyPercent,
                maxCollectorDurationMs =
                    metricSummary.MaxCollectorDurationMs,
                gpuProviders = metricSummary.GpuProviders
            }
        });

        if (!started || exited)
        {
            // Sealing evidence is bounded best-effort work. It must not retain
            // Testing ownership or replace the application's execution outcome.
            _ = await DiagnosticArchive.CreateAsync(resultDirectory, package, job?.Executable,
                runId, preflight?.ExecutableSha256, _config.Diagnostics.CrashReports).ConfigureAwait(false);
        }

        if (job is not null &&
            !(started && !exited))
        {
            var runtimeSuccess =
                assessment.Execution ==
                    ExecutionOutcome.Completed &&
                assessment.Correctness is not
                    (CorrectnessOutcome.Failed or
                     CorrectnessOutcome.Incomplete) &&
                assessment.Evidence is not
                    (EvidenceOutcome.Incomplete or
                     EvidenceOutcome.Invalid);

            RuntimeStateManager.Cleanup(
                job.RuntimeState,
                runtimeState,
                runtimeSuccess,
                resultDirectory,
                targetStopped: !started || exited,
                evidenceFinalized: !started || exited);
        }

        if (preserveTarget &&
            process is not null &&
            launch is not null &&
            job is not null)
        {
            holdPackage = true;
            packageIssue = new QueueIssue(
                "preserved_target",
                Path.GetFileName(package),
                "xemu is intentionally preserved after a runner/control failure. The runner remains available for inspection; use POST /api/v1/xemu/quit or stop xemu externally when finished.",
                DateTimeOffset.UtcNow,
                Retryable: false,
                HoldsTesting: true);

            attempt.Phase = "held";
            AttemptJournal.Write(package, attempt);

            _preservedTarget = new PreservedTarget(
                launch,
                process,
                package,
                attempt,
                job.Id,
                resultDirectory,
                stdout,
                stderr,
                stdoutFile,
                stderrFile,
                job.RuntimeState,
                runtimeState);

            _state.EndJob(
                status,
                job.Id,
                resultDirectory,
                failed: true);
            _state.SetQueueIssue(packageIssue);
            _state.SetPhase("queue_blocked");
            return;
        }

        if ((started && !exited) || componentStuck)
            throw new IOException(detail);

        if (holdPackage)
        {
            attempt.Phase = "held";
            AttemptJournal.Write(package, attempt);
            _state.EndJob(
                status,
                job?.Id ?? Path.GetFileName(package),
                resultDirectory,
                failed:
                    assessment.Execution !=
                        ExecutionOutcome.Completed ||
                    assessment.Correctness is
                        (CorrectnessOutcome.Failed or
                         CorrectnessOutcome.Incomplete) ||
                    assessment.Evidence is
                        (EvidenceOutcome.Incomplete or
                         EvidenceOutcome.Invalid));
            _state.SetQueueIssue(packageIssue);
            _state.SetPhase("queue_blocked");
            return;
        }

        attempt.Phase = "finalized";
        AttemptJournal.Write(package, attempt);
        try
        {
            await _queue.CompleteAsync(
                package,
                TimeSpan.FromMilliseconds(
                    _config.Reliability.ProcessExitTimeoutMs),
                ct).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            packageIssue = new QueueIssue(
                "package_archive_busy",
                Path.GetFileName(package),
                exception.Message,
                DateTimeOffset.UtcNow,
                Retryable: true,
                HoldsTesting: true);
            _state.EndJob(
                status,
                job?.Id ?? Path.GetFileName(package),
                resultDirectory,
                failed:
                    assessment.Execution !=
                        ExecutionOutcome.Completed ||
                    assessment.Correctness is
                        (CorrectnessOutcome.Failed or
                         CorrectnessOutcome.Incomplete) ||
                    assessment.Evidence is
                        (EvidenceOutcome.Incomplete or
                         EvidenceOutcome.Invalid));
            _state.SetQueueIssue(packageIssue);
            _state.SetPhase("queue_blocked");
            return;
        }
        _state.SetQueueIssue(null);
        _state.EndJob(
            status,
            job?.Id ?? Path.GetFileName(package),
            resultDirectory,
            failed:
                assessment.Execution !=
                    ExecutionOutcome.Completed ||
                assessment.Correctness is
                    (CorrectnessOutcome.Failed or
                     CorrectnessOutcome.Incomplete) ||
                assessment.Evidence is
                    (EvidenceOutcome.Incomplete or
                     EvidenceOutcome.Invalid));

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

    private async Task ReapPreservedTargetAsync()
    {
        var held = _preservedTarget;
        if (held is null || !HasExited(held.Process))
            return;

        _preservedTarget = null;
        var processId = held.Process.Id;
        var exitCode = held.Launch.ExitCode;
        _control.End();

        try
        {
            if (held.StandardOutput is not null ||
                held.StandardError is not null)
            {
                await Task.WhenAll(
                    held.StandardOutput ?? Task.CompletedTask,
                    held.StandardError ?? Task.CompletedTask)
                    .WaitAsync(
                        TimeSpan.FromMilliseconds(
                            _config.Reliability.ProcessExitTimeoutMs))
                    .ConfigureAwait(false);
            }
        }
        catch
        {
        }

        if (held.StandardOutputFile is not null)
            await held.StandardOutputFile.DisposeAsync()
                .ConfigureAwait(false);
        if (held.StandardErrorFile is not null)
            await held.StandardErrorFile.DisposeAsync()
                .ConfigureAwait(false);

        await held.Launch.DisposeAsync()
            .ConfigureAwait(false);

        held.Attempt.Phase = "exited";
        AttemptJournal.Write(
            held.Package,
            held.Attempt);

        RuntimeStateManager.Cleanup(
            held.RuntimeDefinition,
            held.RuntimeState,
            success: false,
            held.ResultDirectory,
            targetStopped: true,
            evidenceFinalized: false);

        AtomicJson.Write(
            Path.Combine(
                held.ResultDirectory,
                "preserved-target-exit.json"),
            new
            {
                endedUtc = DateTimeOffset.UtcNow,
                processId,
                exitCode
            });

        _queue.Complete(held.Package);
        _state.SetQueueIssue(null);
        _state.SetQueue(_queue.Snapshot());
        _state.SetPhase("idle");
    }

    private async Task ReleasePreservedTargetHandlesAsync()
    {
        var held = _preservedTarget;
        if (held is null)
            return;

        _preservedTarget = null;

        try
        {
            if (held.StandardOutputFile is not null)
                await held.StandardOutputFile.DisposeAsync()
                    .ConfigureAwait(false);
            if (held.StandardErrorFile is not null)
                await held.StandardErrorFile.DisposeAsync()
                    .ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            await held.Launch.DisposeAsync()
                .ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private sealed record PreservedTarget(
        TargetLaunch Launch,
        Process Process,
        string Package,
        AttemptRecord Attempt,
        string JobId,
        string ResultDirectory,
        Task? StandardOutput,
        Task? StandardError,
        FileStream? StandardOutputFile,
        FileStream? StandardErrorFile,
        RuntimeStateDefinition RuntimeDefinition,
        RuntimeMaterialization? RuntimeState);

    private static PreflightReport AddPreflightFailure(
        PreflightReport report,
        string name,
        string detail) =>
        new(false, report.ExecutableSha256, [.. report.Checks, new(name, false, detail)]);

    // Keep the automatic bundle and fallback screenshot on the same policy.
    // A successful run is not a failure even if a Process observation lags.
    private static bool ShouldCaptureFailure(string status, bool preserveTarget, bool exited) =>
        !preserveTarget && !exited &&
        status is not ("completed" or "cancelled" or "unresponsive");

    private static bool ConfirmTargetExit(bool previousConfirmed, bool currentObserved, bool nativeConfirmed) =>
        previousConfirmed || currentObserved || nativeConfirmed;

    private async Task<(bool Exited, bool TerminationRequested)> StopProcessAsync(Process process)
    {
        var terminationRequested = false;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                terminationRequested = true;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }

        try
        {
            await process.WaitForExitAsync().WaitAsync(
                TimeSpan.FromMilliseconds(_config.Reliability.ProcessExitTimeoutMs))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { }

        return (HasExited(process), terminationRequested);
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

    private sealed class PackageMutationException :
        IOException
    {
        public PackageMutationException(
            string message,
            Exception innerException)
            : base(message, innerException)
        {
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