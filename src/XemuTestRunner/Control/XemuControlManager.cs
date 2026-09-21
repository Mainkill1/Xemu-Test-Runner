using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using XemuTestRunner.Config;
using XemuTestRunner.Queue;

namespace XemuTestRunner.Control;

public sealed class XemuControlManager : IDisposable
{
    private readonly XemuControlOptions _options;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _inputGate = new(1, 1);
    private ActiveSession? _session;

    public XemuControlManager(XemuControlOptions options) => _options = options;

    public bool HasActiveSession
    {
        get { lock (_gate) return _session is not null; }
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
                process.Id,
                _options.QmpHost,
                qmpPort,
                resultDirectory,
                input);
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

    public async Task<string> CaptureScreenshotAsync(string? requestedName, CancellationToken cancellationToken)
    {
        var session = GetSession();
        var screenshotDirectory = Path.Combine(session.ResultDirectory, "screenshots");
        Directory.CreateDirectory(screenshotDirectory);

        var baseName = string.IsNullOrWhiteSpace(requestedName)
            ? $"screenshot-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}"
            : SanitizeFileName(requestedName);

        if (!baseName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            baseName += ".png";

        var path = UniqueFile(screenshotDirectory, baseName);
        var client = new XemuQmpClient(session.QmpHost, session.QmpPort);

        _ = await client.ExecuteAsync(
            "screendump",
            new { filename = path, format = "png" },
            TimeSpan.FromMilliseconds(_options.ScreenshotTimeoutMs),
            cancellationToken).ConfigureAwait(false);

        if (!File.Exists(path))
            throw new IOException("xemu reported screenshot completion but the PNG file was not created.");

        return path;
    }

    public async Task PressButtonAsync(string button, int? holdMs, CancellationToken cancellationToken)
    {
        var session = GetSession();

        if (!_options.ButtonKeys.TryGetValue(button, out var hostKey))
            throw new InvalidDataException($"Unknown Xbox button '{button}'.");

        if (!session.Input.IsAvailable)
            throw new InvalidOperationException($"Configured input provider '{session.Input.Name}' is unavailable.");

        var duration = holdMs.GetValueOrDefault(_options.DefaultButtonHoldMs);
        if (duration <= 0)
            throw new InvalidDataException("Button hold duration must be greater than zero.");

        await _inputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await session.Input.PressAsync(hostKey, duration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _inputGate.Release();
        }
    }

    public async Task ExecutePlanAsync(IReadOnlyList<JobStep> plan, CancellationToken cancellationToken)
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
                    await Task.Delay(step.DelayMs, cancellationToken).ConfigureAwait(false);
                    break;

                case "button":
                    if (step.DelayMs > 0)
                        await Task.Delay(step.DelayMs, cancellationToken).ConfigureAwait(false);
                    await PressButtonAsync(step.Button!, step.DurationMs, cancellationToken).ConfigureAwait(false);
                    break;

                case "screenshot":
                    if (step.DelayMs > 0)
                        await Task.Delay(step.DelayMs, cancellationToken).ConfigureAwait(false);
                    _ = await CaptureScreenshotAsync(step.Name, cancellationToken).ConfigureAwait(false);
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
        }
    }

    public void Dispose()
    {
        End();
        _inputGate.Dispose();
    }

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

    private sealed record ActiveSession(
        int ProcessId,
        string QmpHost,
        int QmpPort,
        string ResultDirectory,
        IXemuInputProvider Input) : IDisposable
    {
        public void Dispose() => Input.Dispose();
    }

    private sealed class UnavailableInputProvider : IXemuInputProvider
    {
        public UnavailableInputProvider(string requested) => Name = $"unavailable:{requested}";
        public string Name { get; }
        public bool IsAvailable => false;
        public Task PressAsync(string hostKey, int holdMs, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Input provider '{Name}' is unavailable.");
        public void Dispose() { }
    }
}
