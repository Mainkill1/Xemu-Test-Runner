using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Control;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;
using XemuTestRunner.Runtime;

namespace XemuTestRunner.Diagnostics;

public sealed class DiagnosticHub
{
    private readonly DiagnosticsOptions _options;
    private readonly XemuControlManager _control;
    private readonly RunnerState _state;
    private readonly ActivityHub _activity;
    private readonly DiagnosticToolCatalog _tools;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private ActiveContext? _active;
    private string? _currentDiagnostic;
    private DateTimeOffset? _currentStartedUtc;
    private readonly List<DiagnosticResult> _completed = [];

    public DiagnosticHub(
        DiagnosticsOptions options,
        XemuControlManager control,
        RunnerState state,
        ActivityHub activity)
    {
        _options = options;
        _control = control;
        _state = state;
        _activity = activity;
        _tools = new DiagnosticToolCatalog(options);
    }

    public DiagnosticToolCatalog Tools => _tools;

    public void Attach(
        string runId,
        Process process,
        RenderDocSession? renderDoc,
        string resultDirectory,
        JobDefinition job,
        CancellationToken runCancellationToken)
    {
        lock (_gate)
        {
            _completed.Clear();
            _currentDiagnostic = null;
            _currentStartedUtc = null;
            _active = new ActiveContext(
                runId,
                process,
                renderDoc,
                resultDirectory,
                job,
                runCancellationToken);
        }
    }

    public void Detach()
    {
        lock (_gate)
        {
            _active = null;
            _currentDiagnostic = null;
            _currentStartedUtc = null;
        }
    }

    public DiagnosticSnapshot Snapshot()
    {
        lock (_gate)
            return new(
                _active is not null,
                _active?.RunId,
                _currentDiagnostic,
                _currentStartedUtc,
                _completed.ToArray());
    }

    public IReadOnlyList<DiagnosticRecipe> Recipes()
    {
        lock (_gate)
            return _active?.Job.Diagnostics.Select(CloneRecipe).ToArray() ?? [];
    }

    public async Task<DiagnosticResult> RunByIdAsync(string id, CancellationToken cancellationToken)
    {
        ActiveContext context;
        DiagnosticRecipe recipe;
        lock (_gate)
        {
            context = _active ?? throw new InvalidOperationException("No active xemu run.");
            recipe = context.Job.Diagnostics.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"Diagnostic recipe not found: {id}");
            recipe = CloneRecipe(recipe);
        }
        return await RunAsync(context, recipe, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DiagnosticResult> RunAdHocAsync(DiagnosticRecipe recipe, CancellationToken cancellationToken)
    {
        recipe.Validate();
        ActiveContext context;
        lock (_gate)
            context = _active ?? throw new InvalidOperationException("No active xemu run.");
        return await RunAsync(context, CloneRecipe(recipe), cancellationToken).ConfigureAwait(false);
    }

    public Task<DiagnosticResult?> AutoHangBundleAsync(string reason, CancellationToken cancellationToken) =>
        RunAutomaticBundleAsync("auto-hang", reason, _options.AutoHangBundle, cancellationToken);

    public Task<DiagnosticResult?> AutoFailureBundleAsync(string reason, CancellationToken cancellationToken) =>
        RunAutomaticBundleAsync("auto-failure", reason, _options.AutoFailureBundle, cancellationToken);

    private async Task<DiagnosticResult?> RunAutomaticBundleAsync(
        string prefix,
        string reason,
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled || !enabled)
            return null;

        ActiveContext context;
        lock (_gate)
            context = _active ?? throw new InvalidOperationException("No active xemu run.");

        var recipe = new DiagnosticRecipe
        {
            Id = prefix + "-" + DateTimeOffset.UtcNow.ToString("HHmmssfff", CultureInfo.InvariantCulture),
            Type = "hang_bundle",
            OutputName = prefix,
            Reason = reason,
            PauseBefore = false,
            ResumeDuring = false,
            PauseAfter = true
        };

        return await RunAsync(
            context,
            recipe,
            cancellationToken,
            linkRunLifetime: false).ConfigureAwait(false);
    }

    public async Task<bool> WaitForIdleAsync(int timeoutMs)
    {
        if (timeoutMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutMs));

        var acquired = await _runGate.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs)).ConfigureAwait(false);
        if (!acquired)
            return false;
        _runGate.Release();
        return true;
    }

    private async Task<DiagnosticResult> RunAsync(
        ActiveContext context,
        DiagnosticRecipe recipe,
        CancellationToken cancellationToken,
        bool linkRunLifetime = true)
    {
        if (!_options.Enabled)
            throw new InvalidOperationException("Diagnostics are disabled.");

        using var linked = linkRunLifetime
            ? CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                context.RunCancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var diagnosticToken = linked.Token;

        await _runGate.WaitAsync(diagnosticToken).ConfigureAwait(false);
        try
        {
            var started = DateTimeOffset.UtcNow;
            var directory = Path.Combine(
                context.ResultDirectory,
                "diagnostics",
                $"{started:yyyyMMdd-HHmmssfff}-{Sanitize(recipe.Id)}");
            Directory.CreateDirectory(directory);

            lock (_gate)
            {
                if (_active?.RunId != context.RunId)
                    throw new InvalidOperationException("Active run changed before diagnostic start.");
                _currentDiagnostic = recipe.Id;
                _currentStartedUtc = started;
            }

            AtomicJson.Write(Path.Combine(directory, "request.json"), recipe);
            _activity.Mark("diagnostic", new { recipe.Id, recipe.Type, recipe.Reason });

            var artifacts = new List<string>();
            string status = "failed";
            string? detail = null;

            try
            {
                if (recipe.PauseBefore && !_control.Snapshot().Paused)
                {
                    await _control.PauseAsync(diagnosticToken).ConfigureAwait(false);
                    _state.SetPhase("paused");
                }

                switch (recipe.Type.Trim().ToLowerInvariant())
                {
                    case "wpr":
                        artifacts.AddRange(await CaptureWprAsync(context, recipe, directory, diagnosticToken).ConfigureAwait(false));
                        break;
                    case "perf":
                        artifacts.AddRange(await CapturePerfAsync(context, recipe, directory, diagnosticToken).ConfigureAwait(false));
                        break;
                    case "renderdoc":
                        artifacts.AddRange(await CaptureRenderDocAsync(context, recipe, directory, diagnosticToken).ConfigureAwait(false));
                        break;
                    case "hang_bundle":
                        artifacts.AddRange(await CaptureHangBundleAsync(context, recipe, directory, diagnosticToken).ConfigureAwait(false));
                        break;
                    case "memory_dump":
                        artifacts.Add(await CaptureMemoryAsync(recipe, directory, diagnosticToken).ConfigureAwait(false));
                        break;
                    case "symbolize":
                        artifacts.AddRange(await SymbolizeAsync(context, recipe, directory, diagnosticToken).ConfigureAwait(false));
                        break;
                    case "qmp":
                        artifacts.Add(await CaptureQmpAsync(recipe, directory, diagnosticToken).ConfigureAwait(false));
                        break;
                    case "monitor":
                        artifacts.Add(await CaptureMonitorAsync(recipe, directory, diagnosticToken).ConfigureAwait(false));
                        break;
                    case "external":
                        artifacts.AddRange(await RunExternalAsync(context, recipe, directory, diagnosticToken).ConfigureAwait(false));
                        break;
                }

                status = "completed";
            }
            catch (OperationCanceledException) when (diagnosticToken.IsCancellationRequested)
            {
                status = "cancelled";
                detail = "Diagnostic cancellation requested.";
                throw;
            }
            catch (Exception ex)
            {
                status = "failed";
                detail = ex.ToString();
                throw;
            }
            finally
            {
                try
                {
                    if ((recipe.PauseAfter || status != "completed") &&
                        _control.HasActiveSession &&
                        !_control.Snapshot().Paused)
                    {
                        await _control.PauseAsync(CancellationToken.None).ConfigureAwait(false);
                        _state.SetPhase("paused");
                    }
                }
                catch (Exception pauseError)
                {
                    detail = (detail ?? "") + " Pause after diagnostic failed: " + pauseError.Message;
                }

                var ended = DateTimeOffset.UtcNow;
                var result = new DiagnosticResult(
                    recipe.Id,
                    recipe.Type,
                    status,
                    started,
                    ended,
                    directory,
                    detail,
                    artifacts.Select(Path.GetFileName).OfType<string>().ToArray());
                AtomicJson.Write(Path.Combine(directory, "result.json"), result);
                lock (_gate)
                {
                    _completed.Add(result);
                    _currentDiagnostic = null;
                    _currentStartedUtc = null;
                }
            }

            lock (_gate)
                return _completed[^1];
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task<IReadOnlyList<string>> CaptureWprAsync(
        ActiveContext context,
        DiagnosticRecipe recipe,
        string directory,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WPR diagnostics require Windows.");

        var instance = "XemuTestRunner-" + Guid.NewGuid().ToString("N");
        var profileParts = recipe.Profile.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (profileParts.Length == 0)
            throw new InvalidDataException("WPR Profile cannot be empty.");

        var startArgs = new List<string>();
        foreach (var profile in profileParts)
        {
            startArgs.Add("-start");
            startArgs.Add(profile);
        }
        startArgs.Add("-filemode");
        startArgs.Add("-instancename");
        startArgs.Add(instance);

        var start = await ToolProcess.RunAsync(
            _options.WprExecutable,
            startArgs,
            directory,
            _options.ToolTimeoutMs,
            ct).ConfigureAwait(false);
        await ToolProcess.WriteRunEvidenceAsync(directory, "wpr-start", start, ct);
        if (start.ExitCode != 0)
            throw new InvalidOperationException($"WPR start failed with exit code {start.ExitCode}.");

        var etl = Path.Combine(directory, SafeOutput(recipe.OutputName, "capture.etl", ".etl"));
        try
        {
            var status = await ToolProcess.RunAsync(
                _options.WprExecutable,
                ["-status", "collectors", "-details", "-instancename", instance],
                directory,
                _options.ToolTimeoutMs,
                ct).ConfigureAwait(false);
            await ToolProcess.WriteRunEvidenceAsync(directory, "wpr-status-start", status, ct);

            await RunTimedWorkloadAsync(recipe, ct).ConfigureAwait(false);

            var stop = await ToolProcess.RunAsync(
                _options.WprExecutable,
                ["-stop", etl, "xemu-test-runner diagnostic", "-instancename", instance],
                directory,
                _options.CaptureFinalizeTimeoutMs,
                ct).ConfigureAwait(false);
            await ToolProcess.WriteRunEvidenceAsync(directory, "wpr-stop", stop, ct);
            if (stop.ExitCode != 0 || !File.Exists(etl))
                throw new InvalidOperationException($"WPR stop failed with exit code {stop.ExitCode}.");
        }
        catch
        {
            try
            {
                _ = await ToolProcess.RunAsync(
                    _options.WprExecutable,
                    ["-cancel", "-instancename", instance],
                    directory,
                    _options.ToolTimeoutMs,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
            throw;
        }

        var artifacts = new List<string> { etl };
        var xperf = ToolProcess.ResolveExecutable(_options.XperfExecutable);
        if (xperf is not null)
        {
            foreach (var analysis in new[]
            {
                (Name: "xperf-tracestats", Args: new[] { "-i", etl, "-a", "tracestats" }),
                (Name: "xperf-profile", Args: new[] { "-i", etl, "-a", "profile", "-detail" }),
                (Name: "xperf-process", Args: new[] { "-i", etl, "-a", "process" })
            })
            {
                var run = await ToolProcess.RunAsync(
                    xperf,
                    analysis.Args,
                    directory,
                    _options.CaptureFinalizeTimeoutMs,
                    ct).ConfigureAwait(false);
                await ToolProcess.WriteRunEvidenceAsync(directory, analysis.Name, run, ct);
                artifacts.Add(Path.Combine(directory, analysis.Name + ".stdout.txt"));
            }
        }
        return artifacts;
    }

    private async Task<IReadOnlyList<string>> CapturePerfAsync(
        ActiveContext context,
        DiagnosticRecipe recipe,
        string directory,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("perf diagnostics require Linux.");

        var perfData = Path.Combine(directory, SafeOutput(recipe.OutputName, "perf.data", ".data"));
        var args = BuildPerfRecordArguments(context.Process.Id, recipe, perfData);

        await using var perfTool = await ToolProcess.StartLongRunningAsync(
            _options.PerfExecutable,
            args,
            directory,
            Path.Combine(directory, "perf-record.stdout.txt"),
            Path.Combine(directory, "perf-record.stderr.txt"),
            ct).ConfigureAwait(false);

        await Task.Delay(250, ct).ConfigureAwait(false);
        if (perfTool.Process.HasExited)
            throw new InvalidOperationException($"perf record exited before workload start with code {perfTool.Process.ExitCode}.");

        try
        {
            await RunTimedWorkloadAsync(recipe, ct).ConfigureAwait(false);
        }
        finally
        {
            if (!perfTool.Process.HasExited)
            {
                try
                {
                    var signal = await ToolProcess.RunAsync(
                        "kill",
                        ["-INT", perfTool.Process.Id.ToString(CultureInfo.InvariantCulture)],
                        directory,
                        _options.ToolTimeoutMs,
                        CancellationToken.None).ConfigureAwait(false);
                    await ToolProcess.WriteRunEvidenceAsync(directory, "perf-stop", signal, CancellationToken.None);
                }
                catch
                {
                    try { perfTool.Process.Kill(entireProcessTree: true); } catch { }
                }
            }
        }

        try
        {
            await perfTool.Process.WaitForExitAsync().WaitAsync(
                TimeSpan.FromMilliseconds(_options.CaptureFinalizeTimeoutMs),
                ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            try { perfTool.Process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        try
        {
            await perfTool.SettleOutputAsync(
                TimeSpan.FromMilliseconds(_options.ToolTimeoutMs)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // perf.data is the primary artifact; preserve a completed trace even if a redirected pipe is slow to settle.
        }

        if (!File.Exists(perfData) || new FileInfo(perfData).Length == 0)
            throw new IOException("perf did not produce a non-empty perf.data.");

        var report = await ToolProcess.RunAsync(
            _options.PerfExecutable,
            ["report", "--stdio", "-i", perfData, "--sort", "comm,dso,symbol", "-n", "--percent-limit", "0.5"],
            directory,
            _options.CaptureFinalizeTimeoutMs,
            ct).ConfigureAwait(false);
        await ToolProcess.WriteRunEvidenceAsync(directory, "perf-report", report, ct);

        return [perfData, Path.Combine(directory, "perf-report.stdout.txt")];
    }

    private async Task<IReadOnlyList<string>> CaptureRenderDocAsync(
        ActiveContext context,
        DiagnosticRecipe recipe,
        string directory,
        CancellationToken ct)
    {
        var session = context.RenderDoc
            ?? throw new InvalidOperationException(
                "RenderDoc diagnostic requires job LaunchMode='renderdoc' so instrumentation is present before xemu graphics initialization.");

        if (recipe.ResumeDuring && _control.Snapshot().Paused)
        {
            await _control.ResumeAsync(ct).ConfigureAwait(false);
            _state.SetPhase("running");
        }

        var output = Path.Combine(directory, SafeOutput(recipe.OutputName, "capture.rdc", ".rdc"));
        var hotkey = recipe.RenderDocTrigger.Equals("xemu-hotkey", StringComparison.OrdinalIgnoreCase);
        Func<CancellationToken, Task>? trigger = null;
        if (hotkey)
        {
            trigger = async token =>
            {
                var keys = new List<string>();
                if (recipe.TracePgraph)
                    keys.Add("ctrl");
                if (recipe.Frames == 5)
                    keys.Add("shift");
                keys.Add("f10");
                await _control.PressHostChordAsync(keys, 100, token).ConfigureAwait(false);
            };
        }

        var captures = await session.CaptureAsync(
            recipe.Frames,
            output,
            hotkey,
            trigger,
            ct).ConfigureAwait(false);

        var artifacts = new List<string>(captures);
        if (recipe.AnalyzeCapture)
        {
            for (var index = 0; index < captures.Count; index++)
            {
                var suffix = captures.Count == 1 ? "" : $"-{index + 1:000}";
                var analysis = Path.Combine(directory, $"renderdoc-summary{suffix}.json");
                var analyzed = await session.AnalyzeAsync(
                    captures[index],
                    analysis,
                    ct).ConfigureAwait(false);
                if (analyzed is not null)
                    artifacts.Add(analyzed);
            }
        }

        if (recipe.PauseAfter && !_control.Snapshot().Paused)
        {
            await _control.PauseAsync(ct).ConfigureAwait(false);
            _state.SetPhase("paused");
        }
        return artifacts;
    }

    private async Task<IReadOnlyList<string>> CaptureHangBundleAsync(
        ActiveContext context,
        DiagnosticRecipe recipe,
        string directory,
        CancellationToken ct)
    {
        var artifacts = new List<string>();
        try
        {
            var qmp = await _control.QueryStatusAsync(ct).ConfigureAwait(false);
            var path = Path.Combine(directory, "qmp-status.json");
            await File.WriteAllTextAsync(path, qmp.GetRawText(), ct).ConfigureAwait(false);
            artifacts.Add(path);
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "qmp-status.error.txt"), ex.ToString(), ct);
        }

        try
        {
            var screenshot = await _control.CaptureScreenshotAsync("hang-bundle", ct, record: false).ConfigureAwait(false);
            var target = Path.Combine(directory, "screen.png");
            File.Copy(screenshot, target, overwrite: true);
            artifacts.Add(target);
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "screenshot.error.txt"), ex.ToString(), ct);
        }

        if (OperatingSystem.IsWindows())
        {
            var dump = Path.Combine(directory, "xemu.dmp");
            var run = await ToolProcess.RunAsync(
                _options.ProcDumpExecutable,
                ["-accepteula", "-ma", context.Process.Id.ToString(CultureInfo.InvariantCulture), dump],
                directory,
                _options.CaptureFinalizeTimeoutMs,
                ct).ConfigureAwait(false);
            await ToolProcess.WriteRunEvidenceAsync(directory, "procdump", run, ct);
            if (File.Exists(dump))
                artifacts.Add(dump);
        }
        else if (OperatingSystem.IsLinux())
        {
            var core = Path.Combine(directory, "xemu.core");
            var backtrace = Path.Combine(directory, "gdb-backtrace.txt");
            var run = await ToolProcess.RunAsync(
                _options.GdbExecutable,
                [
                    "-batch",
                    "-p", context.Process.Id.ToString(CultureInfo.InvariantCulture),
                    "-ex", "set pagination off",
                    "-ex", "set confirm off",
                    "-ex", "thread apply all bt full",
                    "-ex", $"generate-core-file \"{core.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"",
                    "-ex", "detach",
                    "-ex", "quit"
                ],
                directory,
                _options.CaptureFinalizeTimeoutMs,
                ct).ConfigureAwait(false);
            await ToolProcess.WriteRunEvidenceAsync(directory, "gdb", run, ct);
            await File.WriteAllTextAsync(backtrace, run.StandardOutput, ct);
            artifacts.Add(backtrace);
            if (File.Exists(core))
                artifacts.Add(core);
        }
        return artifacts;
    }

    private async Task<string> CaptureMemoryAsync(
        DiagnosticRecipe recipe,
        string directory,
        CancellationToken ct)
    {
        var output = Path.Combine(directory, SafeOutput(recipe.OutputName, "guest-memory.bin", ".bin"));
        var client = _control.CreateQmpClient();
        _ = await client.ExecuteAsync(
            "pmemsave",
            new
            {
                val = recipe.Address,
                size = recipe.Size,
                filename = output
            },
            TimeSpan.FromMilliseconds(_options.CaptureFinalizeTimeoutMs),
            ct).ConfigureAwait(false);

        if (!File.Exists(output) || (ulong)new FileInfo(output).Length != recipe.Size)
            throw new IOException("QMP pmemsave did not produce the expected byte count.");

        await using var stream = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct))
            .ToLowerInvariant();
        AtomicJson.Write(Path.Combine(directory, "memory-dump.json"), new
        {
            address = recipe.Address,
            size = recipe.Size,
            file = Path.GetFileName(output),
            sha256 = hash
        });
        return output;
    }

    private async Task<IReadOnlyList<string>> SymbolizeAsync(
        ActiveContext context,
        DiagnosticRecipe recipe,
        string directory,
        CancellationToken ct)
    {
        var debugFile = JobDefinition.ResolveInsidePackage(
            context.PackageDirectory,
            recipe.DebugFile!);
        if (!File.Exists(debugFile))
            throw new FileNotFoundException("Debug/symbol artifact not found.", debugFile);

        var args = new List<string> { "-a", "-f", "-i", "-C", "-e", debugFile };
        args.AddRange(recipe.Addresses);
        var run = await ToolProcess.RunAsync(
            _options.Addr2LineExecutable,
            args,
            directory,
            _options.ToolTimeoutMs,
            ct).ConfigureAwait(false);
        await ToolProcess.WriteRunEvidenceAsync(directory, "addr2line", run, ct);
        if (run.ExitCode != 0)
            throw new InvalidOperationException($"addr2line failed with exit code {run.ExitCode}.");
        return [Path.Combine(directory, "addr2line.stdout.txt")];
    }

    private async Task<string> CaptureQmpAsync(
        DiagnosticRecipe recipe,
        string directory,
        CancellationToken ct)
    {
        var client = _control.CreateQmpClient();
        object? arguments = recipe.QmpArguments.Count == 0 ? null : recipe.QmpArguments;
        var result = await client.ExecuteAsync(
            recipe.QmpCommand!,
            arguments,
            TimeSpan.FromMilliseconds(_options.CaptureFinalizeTimeoutMs),
            ct).ConfigureAwait(false);
        var path = Path.Combine(directory, "qmp-result.json");
        await File.WriteAllTextAsync(path, result.GetRawText(), ct).ConfigureAwait(false);
        return path;
    }

    private async Task<string> CaptureMonitorAsync(
        DiagnosticRecipe recipe,
        string directory,
        CancellationToken ct)
    {
        var client = _control.CreateQmpClient();
        var result = await client.ExecuteAsync(
            "human-monitor-command",
            new Dictionary<string, object?> { ["command-line"] = recipe.MonitorCommand },
            TimeSpan.FromMilliseconds(_options.CaptureFinalizeTimeoutMs),
            ct).ConfigureAwait(false);
        var path = Path.Combine(directory, "monitor.txt");
        var text = result.ValueKind == JsonValueKind.String ? result.GetString() ?? "" : result.GetRawText();
        await File.WriteAllTextAsync(path, text, ct).ConfigureAwait(false);
        return path;
    }

    private async Task<IReadOnlyList<string>> RunExternalAsync(
        ActiveContext context,
        DiagnosticRecipe recipe,
        string directory,
        CancellationToken ct)
    {
        if (recipe.ResumeDuring && _control.Snapshot().Paused)
        {
            await _control.ResumeAsync(ct).ConfigureAwait(false);
            _state.SetPhase("running");
        }

        var workingDirectory = string.IsNullOrWhiteSpace(recipe.ToolWorkingDirectory)
            ? directory
            : recipe.ToolWorkingDirectory.Equals("diagnostic", StringComparison.OrdinalIgnoreCase)
                ? directory
                : recipe.ToolWorkingDirectory.Equals("result", StringComparison.OrdinalIgnoreCase)
                    ? context.ResultDirectory
                    : JobDefinition.ResolveInsidePackage(context.PackageDirectory, recipe.ToolWorkingDirectory);

        string Expand(string value) => value
            .Replace("{pid}", context.Process.Id.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{diagnosticDir}", directory, StringComparison.OrdinalIgnoreCase)
            .Replace("{resultDir}", context.ResultDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace("{packageDir}", context.PackageDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace("{runId}", context.RunId, StringComparison.OrdinalIgnoreCase);

        var run = await ToolProcess.RunAsync(
            Expand(recipe.ToolExecutable!),
            recipe.ToolArguments.Select(Expand),
            workingDirectory,
            Math.Max(_options.ToolTimeoutMs, recipe.DurationMs),
            ct).ConfigureAwait(false);
        await ToolProcess.WriteRunEvidenceAsync(directory, "external", run, ct).ConfigureAwait(false);
        if (run.ExitCode != 0)
            throw new InvalidOperationException($"External diagnostic exited with code {run.ExitCode}.");

        if (recipe.PauseAfter && !_control.Snapshot().Paused)
        {
            await _control.PauseAsync(ct).ConfigureAwait(false);
            _state.SetPhase("paused");
        }

        return
        [
            Path.Combine(directory, "external.stdout.txt"),
            Path.Combine(directory, "external.stderr.txt"),
            Path.Combine(directory, "external.command.txt")
        ];
    }

    internal static List<string> BuildPerfRecordArguments(
        int processId,
        DiagnosticRecipe recipe,
        string perfData)
    {
        var args = new List<string>
        {
            "record", "-p", processId.ToString(CultureInfo.InvariantCulture)
        };
        if (recipe.ClockId is int clockId)
        {
            args.Add("-k");
            args.Add(clockId.ToString(CultureInfo.InvariantCulture));
        }
        args.AddRange([
            "-g", "--call-graph", recipe.CallGraph,
            "-F", recipe.Frequency.ToString(CultureInfo.InvariantCulture),
            "-o", perfData
        ]);
        return args;
    }

    private async Task RunTimedWorkloadAsync(DiagnosticRecipe recipe, CancellationToken ct)
    {
        if (recipe.ResumeDuring && _control.Snapshot().Paused)
        {
            await _control.ResumeAsync(ct).ConfigureAwait(false);
            _state.SetPhase("running");
        }

        await _control.DelayTestTimeAsync(recipe.DurationMs, ct).ConfigureAwait(false);

        if (recipe.PauseAfter && !_control.Snapshot().Paused)
        {
            await _control.PauseAsync(ct).ConfigureAwait(false);
            _state.SetPhase("paused");
        }
    }

    private static string SafeOutput(string? requested, string fallback, string extension)
    {
        var value = string.IsNullOrWhiteSpace(requested) ? fallback : requested;
        value = string.Concat(value.Select(ch =>
            Path.GetInvalidFileNameChars().Contains(ch) || char.IsWhiteSpace(ch) ? '-' : ch));
        if (!value.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            value += extension;
        return value;
    }

    private static string Sanitize(string value) =>
        string.Concat(value.Select(ch =>
            Path.GetInvalidFileNameChars().Contains(ch) || char.IsWhiteSpace(ch) ? '-' : ch));

    private static DiagnosticRecipe CloneRecipe(DiagnosticRecipe source) => new()
    {
        Id = source.Id,
        Type = source.Type,
        DurationMs = source.DurationMs,
        PauseBefore = source.PauseBefore,
        ResumeDuring = source.ResumeDuring,
        PauseAfter = source.PauseAfter,
        OutputName = source.OutputName,
        Reason = source.Reason,
        Profile = source.Profile,
        Frequency = source.Frequency,
        CallGraph = source.CallGraph,
        ClockId = source.ClockId,
        Frames = source.Frames,
        TracePgraph = source.TracePgraph,
        RenderDocTrigger = source.RenderDocTrigger,
        AnalyzeCapture = source.AnalyzeCapture,
        Address = source.Address,
        Size = source.Size,
        DebugFile = source.DebugFile,
        Addresses = source.Addresses.ToList(),
        QmpCommand = source.QmpCommand,
        QmpArguments = new Dictionary<string, JsonElement>(source.QmpArguments, StringComparer.Ordinal),
        MonitorCommand = source.MonitorCommand,
        ToolExecutable = source.ToolExecutable,
        ToolArguments = source.ToolArguments.ToList(),
        ToolWorkingDirectory = source.ToolWorkingDirectory
    };

    private sealed record ActiveContext(
        string RunId,
        Process Process,
        RenderDocSession? RenderDoc,
        string ResultDirectory,
        JobDefinition Job,
        CancellationToken RunCancellationToken)
    {
        public string PackageDirectory => Job.PackageDirectory
            ?? throw new InvalidOperationException("Job package directory is unavailable.");
    }
}
