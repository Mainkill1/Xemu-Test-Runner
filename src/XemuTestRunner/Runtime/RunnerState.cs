using XemuTestRunner.Monitoring;
using XemuTestRunner.Queue;
using XemuTestRunner.Workstation;

namespace XemuTestRunner.Runtime;

public sealed class RunnerState
{
    private readonly object _gate = new();
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
    private string _phase = "starting";
    private string? _currentJob;
    private string? _runId;
    private int? _processId;
    private DateTimeOffset? _jobStartedUtc;
    private MetricSample? _latestMetric;
    private QueueSnapshot _queue = new(0, 0, 0);
    private string? _httpEndpoint;
    private string? _lastJob;
    private string? _lastResult;
    private DateTimeOffset? _lastFinishedUtc;
    private WorkstationStateSnapshot _workstation = WorkstationStateSnapshot.Unsupported;

    public void SetPhase(string phase) { lock (_gate) _phase = phase; }
    public void SetHttpEndpoint(string? endpoint) { lock (_gate) _httpEndpoint = endpoint; }
    public void SetQueue(QueueSnapshot queue) { lock (_gate) _queue = queue; }
    public void SetWorkstationState(WorkstationStateSnapshot workstation) { lock (_gate) _workstation = workstation; }
    public bool HasActiveJob { get { lock (_gate) return _currentJob is not null; } }

    public void BeginJob(string jobId, string runId, int processId)
    {
        lock (_gate)
        {
            _phase = "running";
            _currentJob = jobId;
            _runId = runId;
            _processId = processId;
            _jobStartedUtc = DateTimeOffset.UtcNow;
            _latestMetric = null;
        }
    }

    public void SetLatestMetric(MetricSample sample) { lock (_gate) _latestMetric = sample; }

    public void EndJob(string result, string? jobId = null)
    {
        lock (_gate)
        {
            _lastJob = jobId ?? _currentJob;
            _lastResult = result;
            _lastFinishedUtc = DateTimeOffset.UtcNow;
            _phase = "idle";
            _currentJob = null;
            _runId = null;
            _processId = null;
            _jobStartedUtc = null;
            _latestMetric = null;
        }
    }

    public RunnerStateSnapshot Snapshot()
    {
        lock (_gate)
        {
            var uptime = DateTimeOffset.UtcNow - _startedUtc;

            return new RunnerStateSnapshot(
                _startedUtc,
                uptime,
                (long)uptime.TotalMilliseconds,
                _phase,
                _currentJob,
                _runId,
                _processId,
                _jobStartedUtc,
                _latestMetric,
                _queue,
                _httpEndpoint,
                _lastJob,
                _lastResult,
                _lastFinishedUtc,
                _workstation);
        }
    }
}

public sealed record RunnerStateSnapshot(
    DateTimeOffset StartedUtc,
    TimeSpan Uptime,
    long UptimeMs,
    string Phase,
    string? CurrentJob,
    string? RunId,
    int? ProcessId,
    DateTimeOffset? JobStartedUtc,
    MetricSample? LatestMetric,
    QueueSnapshot Queue,
    string? HttpEndpoint,
    string? LastJob,
    string? LastResult,
    DateTimeOffset? LastFinishedUtc,
    WorkstationStateSnapshot Workstation);
