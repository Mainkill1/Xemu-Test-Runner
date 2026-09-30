using System.Diagnostics;

namespace XemuTestRunner.Control.Gamepad;

public sealed record ControllerTransition(
    DateTimeOffset RequestedUtc,
    long ReceivedTimestamp,
    NativeGamepadReceipt Receipt);

/// <summary>
/// Owns one OS-visible pad throughout a run. The 40 ms refresh cadence keeps
/// held input current; a missed 250 ms freshness window releases stale controls
/// without disconnecting the controller while this session still owns its pipe.
/// </summary>
public sealed class ControllerInputSession : IAsyncDisposable
{
    private static readonly SemaphoreSlim Ownership = new(1, 1);
    private readonly IXemuGamepadProvider _provider;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private readonly Task _refresh;
    private XboxControllerState _current;
    private Exception? _failure;
    private readonly TaskCompletionSource<Exception> _failureSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;
    private readonly List<ControllerTransition> _transitions = [];
    private long _refreshCount;

    private ControllerInputSession(IXemuGamepadProvider provider, NativeGamepadReceipt initial)
    {
        _provider = provider;
        LastApplied = initial;
        _transitions.Add(new ControllerTransition(DateTimeOffset.UtcNow,
            Stopwatch.GetTimestamp(), initial));
        _current = XboxControllerState.Neutral;
        _refresh = RefreshAsync();
    }

    public IXemuGamepadProvider Provider => _provider;
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public NativeGamepadReceipt LastApplied { get; private set; }
    public Exception? Failure => Volatile.Read(ref _failure);
    public Task<Exception> FailureTask => _failureSignal.Task;
    public bool IsReady => Volatile.Read(ref _disposed) == 0 && Failure is null && _provider.IsReady;
    public long RefreshCount => Interlocked.Read(ref _refreshCount);
    public IReadOnlyList<ControllerTransition> Transitions
    {
        get { lock (_transitions) return _transitions.ToArray(); }
    }

    public void ConfigureTarget(IDictionary<string, string> launchEnvironment)
    {
        ThrowIfUnavailable();
        _provider.ConfigureTarget(launchEnvironment);
    }

    public static async Task<ControllerInputSession> StartAsync(
        IXemuGamepadProvider provider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!await Ownership.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The OS-visible controller is already owned by another runner session.");
        try
        {
            await provider.CreateAsync(1, cancellationToken).ConfigureAwait(false);
            if (!provider.IsReady)
                throw new InvalidOperationException("The native controller did not become ready.");
            var initial = await provider.NeutralizeAsync(cancellationToken).ConfigureAwait(false);
            return new ControllerInputSession(provider, initial);
        }
        catch
        {
            await provider.DisposeAsync().ConfigureAwait(false);
            Ownership.Release();
            throw;
        }
    }

    public async Task<NativeGamepadReceipt> ApplyStateAsync(
        XboxControllerState state,
        CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            var requested = DateTimeOffset.UtcNow;
            var receipt = await _provider.ApplyStateAsync(0, state, cancellationToken).ConfigureAwait(false);
            _current = state;
            LastApplied = receipt;
            lock (_transitions)
                _transitions.Add(new ControllerTransition(requested, Stopwatch.GetTimestamp(), receipt));
            return receipt;
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Volatile.Write(ref _failure, error);
            _failureSignal.TrySetResult(error);
            _lifetime.Cancel();
            throw;
        }
        finally { _writer.Release(); }
    }

    public async Task HoldStateAsync(
        XboxControllerState state,
        int durationMs,
        CancellationToken cancellationToken)
    {
        if (durationMs is < 1 or > 60000)
            throw new ArgumentOutOfRangeException(nameof(durationMs));
        await ApplyStateAsync(state, cancellationToken).ConfigureAwait(false);
        var interval = Task.Delay(durationMs, cancellationToken);
        if (await Task.WhenAny(interval, _refresh).ConfigureAwait(false) == _refresh)
            ThrowIfUnavailable();
        await interval.ConfigureAwait(false);
        ThrowIfUnavailable();
    }

    public async Task PressButtonAsync(
        XboxControllerButtons button,
        int durationMs,
        CancellationToken cancellationToken)
    {
        if (button is XboxControllerButtons.None or XboxControllerButtons.Guide ||
            (button & (button - 1)) != 0)
            throw new InvalidDataException("A button action requires one supported digital control.");
        await PressStateAsync(new XboxControllerState(button, 0, 0, 0, 0, 0, 0),
            durationMs, cancellationToken).ConfigureAwait(false);
    }

    public async Task PressStateAsync(
        XboxControllerState state,
        int durationMs,
        CancellationToken cancellationToken)
    {
        try { await HoldStateAsync(state, durationMs, cancellationToken).ConfigureAwait(false); }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await NeutralizeCoreAsync(cleanup.Token).ConfigureAwait(false);
        }
    }

    public async Task NeutralizeAsync(CancellationToken cancellationToken) =>
        _ = await ApplyStateAsync(XboxControllerState.Neutral, cancellationToken).ConfigureAwait(false);

    public async Task ReleaseAfterInterruptionAsync()
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await NeutralizeCoreAsync(cleanup.Token).ConfigureAwait(false);
    }

    private async Task RefreshAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(40, _lifetime.Token).ConfigureAwait(false);
                await _writer.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try
                {
                    LastApplied = await _provider.ApplyStateAsync(0, _current, _lifetime.Token)
                        .ConfigureAwait(false);
                    Interlocked.Increment(ref _refreshCount);
                }
                finally { _writer.Release(); }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            Volatile.Write(ref _failure, error);
            _failureSignal.TrySetResult(error);
            _lifetime.Cancel();
        }
    }

    private async Task NeutralizeCoreAsync(CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_provider.IsReady)
            {
                var requested = DateTimeOffset.UtcNow;
                LastApplied = await _provider.NeutralizeAsync(cancellationToken).ConfigureAwait(false);
                _current = XboxControllerState.Neutral;
                lock (_transitions)
                    _transitions.Add(new ControllerTransition(requested,
                        Stopwatch.GetTimestamp(), LastApplied));
            }
        }
        finally { _writer.Release(); }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Failure is { } error)
            throw new IOException("Controller input stopped after a failed OS submission.", error);
        if (!_provider.IsReady)
            throw new InvalidOperationException("Native controller is no longer ready.");
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _lifetime.Cancel();
        try
        {
            await _refresh.ConfigureAwait(false);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await NeutralizeCoreAsync(cleanup.Token).ConfigureAwait(false);
        }
        finally
        {
            await _provider.DisposeAsync().ConfigureAwait(false);
            Ownership.Release();
        }
    }
}
