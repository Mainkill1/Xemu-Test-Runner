using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace XemuTestRunner.Authoring;

public sealed record NativeGamepadReceipt(ulong Sequence, long AppliedAtUs, XboxControllerState State);

/// <summary>
/// Owns a user-mode OS gamepad helper, not a custom driver or xemu hook.
/// Send fresh full states regularly (including while holding a state); the native
/// 250ms watchdog removes the device if the supervisor stops sending. This class
/// never manufactures heartbeats that could keep stale browser input alive.
/// </summary>
public sealed class NativeGamepadProvider : IXemuGamepadProvider
{
    private readonly string _executable;
    private readonly string[] _arguments;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _errorGate = new();
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private readonly StringBuilder _errors = new();
    private Process? _process;
    private ProtocolReader? _reader;
    private Task _errorDrain = Task.CompletedTask;
    private ushort _buttons;
    private ulong _sequence;
    private bool _started;
    private int _disposed;
    private volatile bool _ready;

    // Arguments are trusted, local supervisor configuration; never browser data.
    // The shipping native helper accepts none. Fixtures use a separate executable.
    public NativeGamepadProvider(string executablePath, IReadOnlyList<string>? arguments = null,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!Path.IsPathFullyQualified(executablePath))
            throw new ArgumentException("The gamepad helper must have an absolute path.", nameof(executablePath));
        _executable = executablePath;
        _arguments = arguments?.ToArray() ?? [];
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
        if (_timeout < TimeSpan.FromMilliseconds(100) || _timeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public string Name => "native-os-gamepad";
    // Binary availability is not successful OS capability qualification.
    public bool IsAvailable => Volatile.Read(ref _disposed) == 0 && File.Exists(_executable);
    public bool IsReady
    {
        get
        {
            try { return _ready && _process is { HasExited: false }; }
            catch (InvalidOperationException) { return false; }
        }
    }
    public int? WorkerProcessId { get; private set; }
    public string? Backend { get; private set; }
    public NativeGamepadReceipt? LastApplied { get; private set; }

    public async Task CreateAsync(int controllerCount, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("No built-in gamepad provider for this OS.");
        if (controllerCount != 1)
            throw new NotSupportedException("This qualified provider creates one controller per session.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_started) throw new InvalidOperationException("Create a new provider for a new controller session.");
            if (!File.Exists(_executable)) throw new FileNotFoundException("Gamepad helper not installed.", _executable);
            _started = true;
            var launch = new ProcessStartInfo(_executable)
            {
                UseShellExecute = false, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true, StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false, true),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            foreach (string argument in _arguments) launch.ArgumentList.Add(argument);
            using var deadline = Deadline(cancellationToken);
            try
            {
                _process = Process.Start(launch) ?? throw new IOException("Gamepad helper did not start.");
                WorkerProcessId = _process.Id;
                _reader = new ProtocolReader(_process.StandardOutput);
                _errorDrain = DrainErrorsAsync(_process.StandardError);
                using var message = await ReadAsync(deadline.Token).ConfigureAwait(false);
                var root = message.RootElement;
                string expectedBackend = OperatingSystem.IsWindows() ? "windows-input-injector" : "linux-uinput";
                if (root.GetProperty("type").GetString() != "ready" ||
                    root.GetProperty("protocolVersion").GetInt32() != 1 ||
                    root.GetProperty("backend").GetString() != expectedBackend ||
                    root.GetProperty("additionalDriver").GetBoolean() ||
                    !root.GetProperty("systemWide").GetBoolean() ||
                    root.GetProperty("watchdogMs").GetInt32() != 250)
                    throw new InvalidDataException("Unexpected OS gamepad provider/protocol.");
                _buttons = root.GetProperty("supportedButtons").GetUInt16();
                if ((_buttons & 0xf3ff) != 0xf3ff || (_buttons & 0x800) != 0)
                    throw new InvalidDataException("Provider cannot supply the required standard controller buttons.");
                Backend = expectedBackend;
                _ready = true;
            }
            catch (Exception error)
            {
                await StopWorkerAsync(force: true).ConfigureAwait(false);
                throw Failure(error, cancellationToken);
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Call before launching xemu and retain these values in the run's input identity.</summary>
    public void ConfigureTarget(ProcessStartInfo target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ThrowIfDisposed();
        if (OperatingSystem.IsWindows())
        {
            // RawInput can hot-remove this device in remote sessions. XInput is
            // the independently qualified consumer of built-in InputInjector.
            target.Environment["SDL_JOYSTICK_RAWINPUT"] = "0";
        }
        else if (OperatingSystem.IsLinux())
        {
            string path = Path.Combine(Path.GetDirectoryName(_executable)!, "linux-sdl-mapping.txt");
            if (new FileInfo(path).Length > 4096) throw new InvalidDataException("Oversized SDL mapping.");
            string mapping = File.ReadAllText(path).Trim();
            if (!mapping.StartsWith("0600cc4158656d752052756e6e657200,Xemu Runner Gamepad,", StringComparison.Ordinal) ||
                mapping.Contains('\n') || mapping.Contains('\r'))
                throw new InvalidDataException("Unrecognized native gamepad mapping.");
            target.Environment.TryGetValue("SDL_GAMECONTROLLERCONFIG", out string? existing);
            target.Environment["SDL_GAMECONTROLLERCONFIG"] = string.IsNullOrWhiteSpace(existing)
                ? mapping : existing.TrimEnd() + "\n" + mapping;
        }
        else throw new PlatformNotSupportedException("No qualified built-in gamepad provider for this OS.");
    }

    public async Task ApplyStateAsync(int controllerIndex, XboxControllerState state, CancellationToken cancellationToken)
    {
        if (controllerIndex != 0) throw new ArgumentOutOfRangeException(nameof(controllerIndex));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!_ready || _process is null) throw new InvalidOperationException("Gamepad session is not ready.");
            ushort mask = (ushort)state.Buttons;
            if ((mask & ~_buttons) != 0 || (mask & 3) == 3 || (mask & 12) == 12)
                throw new InvalidDataException("Unsupported buttons or opposing D-pad directions.");
            using var deadline = Deadline(cancellationToken);
            try
            {
                ulong sequence = checked(++_sequence);
                string command = FormattableString.Invariant($"state {sequence} {mask} {state.LeftTrigger} {state.RightTrigger} {state.LeftX} {state.LeftY} {state.RightX} {state.RightY}");
                await _process.StandardInput.WriteLineAsync(command.AsMemory(), deadline.Token).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
                using var reply = await ReadAsync(deadline.Token).ConfigureAwait(false);
                var root = reply.RootElement;
                if (root.GetProperty("type").GetString() != "applied" ||
                    root.GetProperty("sequence").GetUInt64() != sequence)
                    throw new InvalidDataException("Gamepad receipt did not match the submitted input.");
                long appliedAtUs = root.GetProperty("appliedAtUs").GetInt64();
                if (appliedAtUs < 0 || appliedAtUs < (LastApplied?.AppliedAtUs ?? 0))
                    throw new InvalidDataException("Gamepad receipt clock moved backwards.");
                LastApplied = new NativeGamepadReceipt(sequence, appliedAtUs, state);
            }
            catch (Exception error)
            {
                await StopWorkerAsync(force: true).ConfigureAwait(false);
                throw Failure(error, cancellationToken);
            }
        }
        finally { _gate.Release(); }
    }

    public Task NeutralizeAsync(CancellationToken cancellationToken) =>
        ApplyStateAsync(0, XboxControllerState.Neutral, cancellationToken);

    private CancellationTokenSource Deadline(CancellationToken token)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        deadline.CancelAfter(_timeout);
        return deadline;
    }
    private async Task<JsonDocument> ReadAsync(CancellationToken token) =>
        JsonDocument.Parse(await _reader!.ReadLineAsync(token).ConfigureAwait(false), new JsonDocumentOptions { MaxDepth = 8 });
    private Exception Failure(Exception error, CancellationToken caller)
    {
        if (caller.IsCancellationRequested) return new OperationCanceledException("Gamepad operation cancelled.", error, caller);
        if (_lifetime.IsCancellationRequested) return new ObjectDisposedException(nameof(NativeGamepadProvider));
        string details; lock (_errorGate) details = _errors.ToString();
        string message = "Gamepad helper failed: " + error.Message + " " + details;
        return error is OperationCanceledException ? new TimeoutException(message, error) : new IOException(message, error);
    }
    private async Task DrainErrorsAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
                lock (_errorGate)
                {
                    _errors.Append(buffer, 0, count);
                    if (_errors.Length > 8192) _errors.Remove(0, _errors.Length - 8192);
                }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
    private async Task StopWorkerAsync(bool force)
    {
        _ready = false;
        Process? process = _process;
        if (process is null) return;
        try
        {
            if (!process.HasExited && !force)
            {
                try
                {
                    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await process.StandardInput.WriteLineAsync("stop".AsMemory(), limit.Token).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(limit.Token).ConfigureAwait(false);
                    await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                }
                catch (IOException) { }
                catch (OperationCanceledException) { }
            }
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await _errorDrain.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        finally
        {
            process.Dispose();
            _process = null;
            _reader = null;
        }
    }
    public ValueTask DisposeAsync()
    {
        // Concurrent callers all wait for confirmed removal, not merely the
        // first caller setting the disposed flag.
        lock (_disposeGate) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopWorkerAsync(force: false).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed class ProtocolReader(StreamReader reader)
    {
        private readonly char[] _buffer = new char[512];
        private int _position, _count;
        public async Task<string> ReadLineAsync(CancellationToken token)
        {
            var text = new StringBuilder(256);
            while (true)
            {
                if (_position == _count)
                {
                    _count = await reader.ReadAsync(_buffer.AsMemory(), token).ConfigureAwait(false);
                    _position = 0;
                    if (_count == 0) throw new EndOfStreamException("Gamepad helper closed its output.");
                }
                char value = _buffer[_position++];
                if (value == '\n') return text.ToString().TrimEnd('\r');
                if (text.Length >= 4096) throw new InvalidDataException("Oversized gamepad reply.");
                text.Append(value);
            }
        }
    }
}
