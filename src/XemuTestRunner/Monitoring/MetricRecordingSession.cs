using System.Threading.Channels;
using XemuTestRunner.Config;

namespace XemuTestRunner.Monitoring;

/// <summary>
/// Owns one run's bounded sample queue, CSV handle, and shared finalization task.
/// Sampling never waits for disk; stopping waits for every accepted row to drain.
/// </summary>
internal sealed class MetricRecordingSession
{
    private readonly object _stateLock = new();
    private readonly Channel<MetricSample> _samples;
    private readonly Task _writerTask;

    private bool _closed;
    private long _observedSamples;
    private long _writtenSamples;
    private long _overrunCount;
    private long _droppedWriteSamples;
    private double _totalCollectorDutyPercent;
    private double _maximumCollectorDutyPercent;
    private double _maximumCollectorDurationMs;

    public MetricRecordingSession(MonitoringOptions options, string csvPath)
    {
        var path = Path.GetFullPath(csvPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        _samples = Channel.CreateBounded<MetricSample>(
            new BoundedChannelOptions(options.BufferCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });

        // Open before the collector attaches the process. A path/permission
        // failure must leave it idle, not attached to a half-created recording.
        var file = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        _writerTask = MetricCsvWriter.WriteAsync(
            _samples.Reader,
            file,
            options.FlushIntervalMs,
            RecordWrittenSample);
    }

    public Task Completion => _writerTask;

    public void Publish(MetricSample sample)
    {
        lock (_stateLock)
        {
            if (_closed)
            {
                return;
            }

            RecordObservedSample(sample);

            // Wait mode plus TryWrite reports a full queue without blocking or
            // silently evicting a previously accepted row. Do not use WriteAsync
            // here: disk backpressure must not stall the hardware sampler.
            if (!_samples.Writer.TryWrite(sample))
            {
                _droppedWriteSamples++;
            }
        }
    }

    public Task StopAsync()
    {
        lock (_stateLock)
        {
            if (!_closed)
            {
                _closed = true;
                _samples.Writer.TryComplete();
            }

            // Every caller observes the same completion, including any failure.
            // Cancelling the reader instead would discard accepted samples.
            return _writerTask;
        }
    }

    public MetricRecordingSummary GetSummary(IReadOnlyList<string> gpuProviders)
    {
        if (!_writerTask.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException(
                "Recording must finish successfully before reading its summary.");
        }

        lock (_stateLock)
        {
            var averageCollectorDutyPercent = _observedSamples == 0
                ? 0
                : _totalCollectorDutyPercent / _observedSamples;

            return new MetricRecordingSummary(
                Interlocked.Read(ref _writtenSamples),
                _overrunCount,
                _droppedWriteSamples,
                averageCollectorDutyPercent,
                _maximumCollectorDutyPercent,
                _maximumCollectorDurationMs,
                gpuProviders)
            {
                ObservedSamples = _observedSamples
            };
        }
    }

    // Called under _stateLock by the single producer. Include dropped samples
    // in collector-cost statistics: their hardware reads still consumed time.
    private void RecordObservedSample(MetricSample sample)
    {
        _observedSamples++;
        _totalCollectorDutyPercent += sample.CollectorDutyPercent;
        _maximumCollectorDutyPercent = Math.Max(
            _maximumCollectorDutyPercent,
            sample.CollectorDutyPercent);
        _maximumCollectorDurationMs = Math.Max(
            _maximumCollectorDurationMs,
            sample.CollectorDurationMs);

        if (sample.Overrun)
        {
            _overrunCount++;
        }
    }

    private void RecordWrittenSample()
    {
        // The consumer runs independently. These rows are not final evidence
        // until Completion succeeds, including the writer's final flush.
        Interlocked.Increment(ref _writtenSamples);
    }
}
