namespace XemuTestRunner.Authoring;

public enum RunnerActivityKind
{
    Idle,
    TestExecution,
    TestAuthoring
}

public sealed record RunnerActivitySnapshot(
    RunnerActivityKind Kind,
    string? OwnerId,
    DateTimeOffset? AcquiredUtc)
{
    public bool IsBusy => Kind != RunnerActivityKind.Idle;
}

public sealed record RunnerActivityConflict(
    RunnerActivityKind Requested,
    RunnerActivityKind Active,
    string ActiveOwnerId,
    DateTimeOffset AcquiredUtc);

public sealed class RunnerActivityCoordinator
{
    private readonly object _gate = new();
    private RunnerActivityKind _kind;
    private string? _ownerId;
    private DateTimeOffset? _acquiredUtc;
    private Guid _leaseToken;

    public RunnerActivitySnapshot Snapshot()
    {
        lock (_gate)
            return new RunnerActivitySnapshot(_kind, _ownerId, _acquiredUtc);
    }

    public bool TryAcquire(
        RunnerActivityKind kind,
        string ownerId,
        out RunnerActivityLease? lease,
        out RunnerActivityConflict? conflict)
    {
        if (kind == RunnerActivityKind.Idle)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Idle cannot own the runner.");
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new ArgumentException("An activity owner ID is required.", nameof(ownerId));

        lock (_gate)
        {
            if (_kind != RunnerActivityKind.Idle)
            {
                lease = null;
                conflict = new RunnerActivityConflict(
                    kind,
                    _kind,
                    _ownerId!,
                    _acquiredUtc!.Value);
                return false;
            }

            var token = Guid.NewGuid();
            var acquiredUtc = DateTimeOffset.UtcNow;
            _kind = kind;
            _ownerId = ownerId;
            _acquiredUtc = acquiredUtc;
            _leaseToken = token;

            lease = new RunnerActivityLease(this, token, kind, ownerId, acquiredUtc);
            conflict = null;
            return true;
        }
    }

    internal void Release(Guid token)
    {
        lock (_gate)
        {
            if (_kind == RunnerActivityKind.Idle || token != _leaseToken)
                return;

            _kind = RunnerActivityKind.Idle;
            _ownerId = null;
            _acquiredUtc = null;
            _leaseToken = Guid.Empty;
        }
    }
}

public sealed class RunnerActivityLease : IDisposable, IAsyncDisposable
{
    private RunnerActivityCoordinator? _coordinator;
    private readonly Guid _token;

    internal RunnerActivityLease(
        RunnerActivityCoordinator coordinator,
        Guid token,
        RunnerActivityKind kind,
        string ownerId,
        DateTimeOffset acquiredUtc)
    {
        _coordinator = coordinator;
        _token = token;
        Kind = kind;
        OwnerId = ownerId;
        AcquiredUtc = acquiredUtc;
    }

    public RunnerActivityKind Kind { get; }
    public string OwnerId { get; }
    public DateTimeOffset AcquiredUtc { get; }

    public void Dispose()
    {
        var coordinator = Interlocked.Exchange(ref _coordinator, null);
        coordinator?.Release(_token);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
