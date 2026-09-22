using System.Threading.Channels;
using XemuTestRunner.Config;

namespace XemuTestRunner.Monitoring;

/// <summary>Owns one recording's queue, output handle, and finalization task.</summary>
internal sealed class MetricRecordingSession
{
    private readonly object _gate = new();
    private readonly Channel<MetricSample> _samples;
    private readonly Task _writerTask;
    private bool _closed;
    private long _observed;
    private long _written;
    private long _overruns;
    private long _dropped;
    private double _totalDuty;
    private double _maximumDuty;
    private double _maximumDuration;

    public MetricRecordingSession(MonitoringOptions options, string csvPath)
    {
        var path = Path.GetFullPath(csvPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        _samples = Channel.CreateBounded<MetricSample>(new BoundedChannelOptions(options.BufferCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        // Opening is synchronous and happens before the collector attaches the
        // process. A permission/path failure cannot leave a half-active recording.
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        _writerTask = MetricCsvWriter.WriteAsync(
            _samples.Reader, file, options.FlushIntervalMs,
            () => Interlocked.Increment(ref _written));
    }

    public Task Completion => _writerTask;

    public void Publish(MetricSample sample)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _observed++;
            _totalDuty += sample.CollectorDutyPercent;
            _maximumDuty = Math.Max(_maximumDuty, sample.CollectorDutyPercent);
            _maximumDuration = Math.Max(_maximumDuration, sample.CollectorDurationMs);
            if (sample.Overrun)
            {
                _overruns++;
            }

            // The sampler must never wait for disk. The writer is supervised by
            // RunnerEngine; failed writes propagate through Completion/StopAsync.
            if (!_samples.Writer.TryWrite(sample))
            {
                _dropped++;
            }
        }
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            if (!_closed)
            {
                _closed = true;
                _samples.Writer.TryComplete();
            }

            // Every stop caller awaits the SAME writer, including its failure.
            return _writerTask;
        }
    }

    public MetricRecordingSummary GetSummary(IReadOnlyList<string> gpuProviders)
    {
        if (!_writerTask.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException("Recording must finish successfully before reading its summary.");
        }

        lock (_gate)
        {
            return new MetricRecordingSummary(
                Interlocked.Read(ref _written), _overruns, _dropped,
                _observed == 0 ? 0 : _totalDuty / _observed,
                _maximumDuty, _maximumDuration, gpuProviders)
            {
                ObservedSamples = _observed
            };
        }
    }
}
