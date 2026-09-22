using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Queue;
using XemuTestRunner.Runtime;

namespace XemuTestRunner.Control;

public sealed class XemuControlManager : IDisposable
{
    private readonly XemuControlOptions _options;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _inputGate = new(1, 1);
    private readonly SemaphoreSlim _screenshotGate = new(1, 1);
    private ActiveSession? _session;
    private bool _paused;
    private TaskCompletionSource<bool> _resumeSignal = CompletedSignal();
    private bool _recording;
    private DateTimeOffset? _recordingStartedUtc;
    private DateTimeOffset? _recordingLastActionEndUtc;
    private string? _recordingSavedFile;
    private bool _quitRequested;
    private readonly List<JobStep> _recordedSteps = [];

    public XemuControlManager(XemuControlOptions options) => _options = options;

    public bool HasActiveSession
    {
        get { lock (_gate) return _session is not null; }
    }

    public ControlSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new ControlSnapshot(
                _session is not null,
                _paused,
                _session?.ProcessId,
                _session?.QmpHost,
                _session?.QmpPort,
                _session?.Input.Name,
                _session?.Input.IsAvailable ?? false,
                _recording,
                _recordingStartedUtc,
                _recordedSteps.Count);
        }
    }

    public int AllocateQmpPort()
    {
        if (_options.QmpPort > 0)
            return _options.QmpPort;

        var address = IPAddress.TryParse(_options.QmpHost, out var parsed)
            ? parsed
            : IPAddress.Loopback;

        var listener = new TcpListener(address, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public void Begin(Process process, string resultDirectory, int qmpPort)
    {
        IXemuInputProvider input = CreateInputProvider(process.Id);

        lock (_gate)
        {
            _session?.Dispose();
            _session = new ActiveSession(
                process,
                _options.QmpHost,
                qmpPort,
                resultDirectory,
                input);
            _paused = false;
            _resumeSignal.TrySetResult(true);
            _resumeSignal = CompletedSignal();
            _recording = false;
            _recordingStartedUtc = null;
            _recordingLastActionEndUtc = null;
            _recordingSavedFile = null;
            _recordedSteps.Clear();
            _quitRequested = false;
        }
    }

    public async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        var session = GetSession();
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(_options.ConnectTimeoutMs);
        Exception? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var client = new XemuQmpClient(session.QmpHost, session.QmpPort);
                _ = await client.ExecuteAsync(
                    "query-status",
                    null,
                    TimeSpan.FromMilliseconds(Math.Min(1500, _options.ConnectTimeoutMs)),
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException or InvalidOperationException or OperationCanceledException)
            {
                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    throw;

                last = ex;
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new TimeoutException(
            $"xemu QMP did not become ready at {_options.QmpHost}:{session.QmpPort} within {_options.ConnectTimeoutMs} ms.",
            last);
    }

    public XemuQmpClient CreateQmpClient()
    {
        var session = GetSession();
        return new XemuQmpClient(session.QmpHost, session.QmpPort);
    }

    public async Task<JsonElement> QueryStatusAsync(CancellationToken cancellationToken)
    {
        var client = CreateQmpClient();
        return await client.ExecuteAsync(
            "query-status",
            null,
            TimeSpan.FromMilliseconds(_options.ConnectTimeoutMs),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshPauseStateAsync(CancellationToken cancellationToken)
    {
        var status = await QueryStatusAsync(cancellationToken).ConfigureAwait(false);
        var paused = status.TryGetProperty("status", out var value) &&
            value.GetString()?.Equals("paused", StringComparison.OrdinalIgnoreCase) == true;

        lock (_gate)
        {
            _paused = paused;
            if (paused)
            {
                if (_resumeSignal.Task.IsCompleted)
                    _resumeSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            else
            {
                _resumeSignal.TrySetResult(true);
                _resumeSignal = CompletedSignal();
            }
        }
    }

    public async Task PauseAsync(CancellationToken cancellationToken)
    {
        var session = GetSession();
        lock (_gate)
        {
            if (_paused)
                return;
        }

        var client = new XemuQmpClient(session.QmpHost, session.QmpPort);
        _ = await client.ExecuteAsync(
            "stop",
            null,
            TimeSpan.FromMilliseconds(_options.ConnectTimeoutMs),
            cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            if (!_paused)
            {
                _paused = true;
                _resumeSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken)
    {
        var session = GetSession();
        lock (_gate)
        {
            if (!_paused)
                return;
        }

        var client = new XemuQmpClient(session.QmpHost, session.QmpPort);
        _ = await client.ExecuteAsync(
            "cont",
            null,
            TimeSpan.FromMilliseconds(_options.ConnectTimeoutMs),
            cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _paused = false;
            _resumeSignal.TrySetResult(true);
            _resumeSignal = CompletedSignal();
        }
    }

    public async Task QuitAsync(CancellationToken cancellationToken)
    {
        var session = GetSession();
        var client = new XemuQmpClient(session.QmpHost, session.QmpPort);
        lock (_gate)
            _quitRequested = true;
        try
        {
            _ = await client.ExecuteAsync(
                "quit",
                null,
                TimeSpan.FromMilliseconds(_options.ConnectTimeoutMs),
                cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // xemu commonly closes QMP as part of a successful quit without
            // returning the command result. Only accept that disconnect after
            // the owned target process has actually exited.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_options.ConnectTimeoutMs);
            try
            {
                await session.Process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException("QMP disconnected after quit, but xemu did not exit within the control timeout.");
            }
        }
    }

    public async Task<string> CaptureScreenshotAsync(
        string? requestedName,
        CancellationToken cancellationToken,
        bool record = true)
    {
        var session = GetSession();
        var started = DateTimeOffset.UtcNow;
        var screenshotDirectory = Path.Combine(session.ResultDirectory, "screenshots");
        Directory.CreateDirectory(screenshotDirectory);

        var baseName = string.IsNullOrWhiteSpace(requestedName)
            ? $"screenshot-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}"
            : SanitizeFileName(requestedName);

        if (!baseName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            baseName += ".png";

        var path = UniqueFile(screenshotDirectory, baseName);
        await CaptureImageAsync(session, path, cancellationToken).ConfigureAwait(false);

        if (record)
        {
            var ended = DateTimeOffset.UtcNow;
            RecordManualStep(
                new JobStep
                {
                    Type = "screenshot",
                    Name = Path.GetFileNameWithoutExtension(path)
                },
                started,
                ended);
        }

        return path;
    }

    public async Task<string> CapturePreviewAsync(CancellationToken cancellationToken)
    {
        var session = GetSession();
        var previewDirectory = Path.Combine(session.ResultDirectory, ".preview");
        Directory.CreateDirectory(previewDirectory);
        var path = Path.Combine(previewDirectory, $"preview-{Guid.NewGuid():N}.png");
        await CaptureImageAsync(session, path, cancellationToken).ConfigureAwait(false);
        return path;
    }

    public async Task PressButtonAsync(
        string button,
        int? holdMs,
        CancellationToken cancellationToken,
        bool record = true)
    {
        var session = GetSession();

        if (!_options.ButtonKeys.TryGetValue(button, out var hostKey))
            throw new InvalidDataException($"Unknown Xbox button '{button}'.");

        if (!session.Input.IsAvailable)
            throw new InvalidOperationException($"Configured input provider '{session.Input.Name}' is unavailable.");

        var duration = holdMs.GetValueOrDefault(_options.DefaultButtonHoldMs);
        if (duration <= 0)
            throw new InvalidDataException("Button hold duration must be greater than zero.");

        await WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);

        var started = DateTimeOffset.UtcNow;
        await _inputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await session.Input.PressAsync(hostKey, duration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _inputGate.Release();
        }

        if (record)
        {
            RecordManualStep(
                new JobStep
                {
                    Type = "button",
                    Button = button,
                    DurationMs = duration
                },
                started,
                DateTimeOffset.UtcNow);
        }
    }

    public async Task PressHostChordAsync(
        IReadOnlyList<string> hostKeys,
        int holdMs,
        CancellationToken cancellationToken)
    {
        var session = GetSession();
        if (!session.Input.IsAvailable)
            throw new InvalidOperationException($"Configured input provider '{session.Input.Name}' is unavailable.");
        if (holdMs is < 1 or > 60000)
            throw new InvalidDataException("Host chord hold duration must be between 1 and 60000 ms.");

        await _inputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await session.Input.PressChordAsync(hostKeys, holdMs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _inputGate.Release();
        }
    }

    public RecordedPlanSnapshot StartRecording()
    {
        lock (_gate)
        {
            if (_session is null)
                throw new InvalidOperationException("No xemu test is currently active.");

            _recordedSteps.Clear();
            _recording = true;
            _recordingStartedUtc = DateTimeOffset.UtcNow;
            _recordingLastActionEndUtc = _recordingStartedUtc;
            _recordingSavedFile = null;
            return RecordingSnapshotUnsafe();
        }
    }

    public RecordedPlanSnapshot StopRecording()
    {
        lock (_gate)
        {
            _recording = false;

            if (_session is not null && _recordedSteps.Count > 0)
            {
                var path = Path.Combine(
                    _session.ResultDirectory,
                    $"recorded-plan-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.json");

                File.WriteAllText(
                    path,
                    JsonSerializer.Serialize(
                        new { Plan = _recordedSteps.Select(CloneStep).ToArray() },
                        ConfigLoader.JsonOptions));

                _recordingSavedFile = path;
            }

            return RecordingSnapshotUnsafe();
        }
    }

    public RecordedPlanSnapshot ClearRecording()
    {
        lock (_gate)
        {
            _recordedSteps.Clear();
            _recordingSavedFile = null;
            _recordingLastActionEndUtc = _recording ? DateTimeOffset.UtcNow : null;
            return RecordingSnapshotUnsafe();
        }
    }

    public RecordedPlanSnapshot RecordingSnapshot()
    {
        lock (_gate)
            return RecordingSnapshotUnsafe();
    }

    public Task DelayTestTimeAsync(int delayMs, CancellationToken cancellationToken) =>
        DelayRespectingPauseAsync(delayMs, cancellationToken);

    public async Task ExecutePlanAsync(
        IReadOnlyList<JobStep> plan,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, Task>? diagnosticRunner = null,
        Action<string>? segmentStart = null,
        Action<string>? segmentEnd = null,
        Func<ArtifactCheckDefinition, int, int, CancellationToken, Task>? artifactWaiter = null)
    {
        if (plan.Count == 0)
            return;

        await WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);

        foreach (var step in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (step.Type.Trim().ToLowerInvariant())
            {
                case "wait":
                    await DelayRespectingPauseAsync(step.DelayMs, cancellationToken).ConfigureAwait(false);
                    break;

                case "button":
                    if (step.DelayMs > 0)
                        await DelayRespectingPauseAsync(step.DelayMs, cancellationToken).ConfigureAwait(false);

                    await PressButtonAsync(
                        step.Button!,
                        step.DurationMs,
                        cancellationToken,
                        record: false).ConfigureAwait(false);
                    break;

                case "screenshot":
                    if (step.DelayMs > 0)
                        await DelayRespectingPauseAsync(step.DelayMs, cancellationToken).ConfigureAwait(false);

                    _ = await CaptureScreenshotAsync(
                        step.Name,
                        cancellationToken,
                        record: false).ConfigureAwait(false);
                    break;

                case "pause":
                    await PauseAsync(cancellationToken).ConfigureAwait(false);
                    break;

                case "resume":
                    await ResumeAsync(cancellationToken).ConfigureAwait(false);
                    break;

                case "quit":
                    await QuitAsync(cancellationToken).ConfigureAwait(false);
                    break;

                case "require_input":
                    if (!Snapshot().InputAvailable)
                        throw new InvalidOperationException("Plan requires controller input but the configured input provider is unavailable.");
                    break;

                case "diagnostic":
                    if (diagnosticRunner is null)
                        throw new InvalidOperationException("Plan contains a diagnostic step but no diagnostic runner is attached.");
                    await diagnosticRunner(step.DiagnosticId!, cancellationToken).ConfigureAwait(false);
                    break;

                case "segment_start":
                    if (segmentStart is null)
                        throw new InvalidOperationException("Plan contains segment_start but no measurement timeline is attached.");
                    segmentStart(step.Name!);
                    break;

                case "segment_end":
                    if (segmentEnd is null)
                        throw new InvalidOperationException("Plan contains segment_end but no measurement timeline is attached.");
                    segmentEnd(step.Name!);
                    break;

                case "wait_for_artifact":
                    if (artifactWaiter is null || step.Condition is null)
                        throw new InvalidOperationException(
                            "Plan contains wait_for_artifact but no condition waiter is attached.");
                    await artifactWaiter(
                        step.Condition,
                        step.TimeoutMs,
                        step.PollIntervalMs,
                        cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    public void End()
    {
        lock (_gate)
        {
            _session?.Dispose();
            _session = null;
            _paused = false;
            _resumeSignal.TrySetResult(true);
            _resumeSignal = CompletedSignal();
            _recording = false;
            _recordingStartedUtc = null;
            _recordingLastActionEndUtc = null;
            _quitRequested = false;
        }
    }

    public bool QuitRequested
    {
        get { lock (_gate) return _quitRequested; }
    }

    public void Dispose()
    {
        End();
        _inputGate.Dispose();
        _screenshotGate.Dispose();
    }

    private async Task CaptureImageAsync(
        ActiveSession session,
        string path,
        CancellationToken cancellationToken)
    {
        await _screenshotGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var provider = _options.ScreenshotProvider.Trim().ToLowerInvariant();
            if (provider != "external")
            {
                try
                {
                    var client = new XemuQmpClient(session.QmpHost, session.QmpPort);
                    _ = await client.ExecuteAsync(
                        "screendump",
                        new { filename = path, format = "png" },
                        TimeSpan.FromMilliseconds(_options.ScreenshotTimeoutMs),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (QmpCommandException ex) when (provider == "auto")
                {
                    if (string.IsNullOrWhiteSpace(_options.ScreenshotExecutable))
                    {
                        throw new InvalidOperationException(
                            "This xemu build does not provide QMP screenshots and no external screenshot command is configured.",
                            ex);
                    }

                    await CaptureExternalImageAsync(session, path, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await CaptureExternalImageAsync(session, path, cancellationToken).ConfigureAwait(false);
            }

            await ValidatePngAsync(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _screenshotGate.Release();
        }
    }

    private static async Task ValidatePngAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            throw new IOException("Screenshot capture completed but the PNG file was not created.");

        var signature = new byte[8];
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            signature.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            await stream.ReadExactlyAsync(signature, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException("Screenshot command did not create a valid PNG file.");
        }
        if (!signature.AsSpan().SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }))
            throw new InvalidDataException("Screenshot command did not create a valid PNG file.");
    }

    private async Task CaptureExternalImageAsync(
        ActiveSession session,
        string path,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(_options.ScreenshotExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = session.ResultDirectory
        };
        foreach (var argument in _options.ScreenshotArguments)
        {
            startInfo.ArgumentList.Add(argument
                .Replace("{path}", path, StringComparison.Ordinal)
                .Replace("{pid}", session.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        }

        Process? started;
        try
        {
            started = Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new IOException($"Failed to start screenshot command '{_options.ScreenshotExecutable}'.", ex);
        }
        using var process = started
            ?? throw new IOException($"Failed to start screenshot command '{_options.ScreenshotExecutable}'.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.ScreenshotTimeoutMs);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                _ = await stdout.ConfigureAwait(false);
                _ = await stderr.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
            {
            }
            throw new TimeoutException($"Screenshot command exceeded {_options.ScreenshotTimeoutMs} ms.");
        }

        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new IOException($"Screenshot command exited with code {process.ExitCode}: {detail.Trim()}");
        }
    }

    private async Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        Task waitTask;
        lock (_gate)
            waitTask = _paused ? _resumeSignal.Task : Task.CompletedTask;

        await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DelayRespectingPauseAsync(int delayMs, CancellationToken cancellationToken)
    {
        var remaining = TimeSpan.FromMilliseconds(delayMs);

        while (remaining > TimeSpan.Zero)
        {
            await WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);

            var slice = remaining > TimeSpan.FromMilliseconds(100)
                ? TimeSpan.FromMilliseconds(100)
                : remaining;

            var started = Stopwatch.GetTimestamp();
            await Task.Delay(slice, cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                if (!_paused)
                    remaining -= Stopwatch.GetElapsedTime(started);
            }
        }
    }

    private void RecordManualStep(JobStep step, DateTimeOffset started, DateTimeOffset ended)
    {
        lock (_gate)
        {
            if (!_recording)
                return;

            var previousEnd = _recordingLastActionEndUtc ?? _recordingStartedUtc ?? started;
            var gap = Math.Max(0, (int)Math.Round((started - previousEnd).TotalMilliseconds));

            if (gap >= 25)
            {
                _recordedSteps.Add(new JobStep
                {
                    Type = "wait",
                    DelayMs = gap
                });
            }

            _recordedSteps.Add(CloneStep(step));
            _recordingLastActionEndUtc = ended;
        }
    }

    private RecordedPlanSnapshot RecordingSnapshotUnsafe() => new(
        _recording,
        _recordingStartedUtc,
        _recordedSteps.Select(CloneStep).ToArray(),
        _recordingSavedFile);

    private ActiveSession GetSession()
    {
        lock (_gate)
            return _session ?? throw new InvalidOperationException("No xemu test is currently active.");
    }

    private IXemuInputProvider CreateInputProvider(int processId)
    {
        var requested = _options.InputProvider.Trim().ToLowerInvariant();

        if (requested is "auto" or "windows" or "sendinput")
        {
            var windows = new WindowsKeyboardInputProvider(processId);
            if (windows.IsAvailable)
                return windows;
            windows.Dispose();
        }

        if (requested is "auto" or "linux" or "x11" or "xtest")
        {
            var linux = new LinuxX11InputProvider(processId);
            if (linux.IsAvailable)
                return linux;
            linux.Dispose();
        }

        return new UnavailableInputProvider(requested);
    }

    private static JobStep CloneStep(JobStep step) => new()
    {
        Type = step.Type,
        DelayMs = step.DelayMs,
        Button = step.Button,
        DurationMs = step.DurationMs,
        Name = step.Name,
        DiagnosticId = step.DiagnosticId,
        Condition = step.Condition,
        TimeoutMs = step.TimeoutMs,
        PollIntervalMs = step.PollIntervalMs
    };

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = string.Concat(value.Select(ch => invalid.Contains(ch) || char.IsWhiteSpace(ch) ? '-' : ch));
        return string.IsNullOrWhiteSpace(result) ? "screenshot" : result;
    }

    private static string UniqueFile(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        if (!File.Exists(path))
            return path;

        return Path.Combine(
            directory,
            $"{Path.GetFileNameWithoutExtension(name)}-{DateTimeOffset.UtcNow:HHmmssfff}{Path.GetExtension(name)}");
    }

    private static TaskCompletionSource<bool> CompletedSignal()
    {
        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.TrySetResult(true);
        return signal;
    }

    private sealed record ActiveSession(
        Process Process,
        string QmpHost,
        int QmpPort,
        string ResultDirectory,
        IXemuInputProvider Input) : IDisposable
    {
        public int ProcessId => Process.Id;
        public void Dispose() => Input.Dispose();
    }

    private sealed class UnavailableInputProvider : IXemuInputProvider
    {
        public UnavailableInputProvider(string requested) => Name = $"unavailable:{requested}";
        public string Name { get; }
        public bool IsAvailable => false;
        public Task PressAsync(string hostKey, int holdMs, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Input provider '{Name}' is unavailable.");
        public Task PressChordAsync(IReadOnlyList<string> hostKeys, int holdMs, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Input provider '{Name}' is unavailable.");
        public void Dispose() { }
    }
}

public sealed record ControlSnapshot(
    bool Active,
    bool Paused,
    int? ProcessId,
    string? QmpHost,
    int? QmpPort,
    string? InputProvider,
    bool InputAvailable,
    bool Recording,
    DateTimeOffset? RecordingStartedUtc,
    int RecordedSteps);

public sealed record RecordedPlanSnapshot(
    bool Recording,
    DateTimeOffset? StartedUtc,
    IReadOnlyList<JobStep> Plan,
    string? SavedFile);
