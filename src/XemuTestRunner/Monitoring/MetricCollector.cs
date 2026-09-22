using System.Diagnostics;
using XemuTestRunner.Config;
using XemuTestRunner.Monitoring.Providers;

namespace XemuTestRunner.Monitoring;

/// <summary>
/// Samples for the runner's lifetime. Recording is an optional, run-scoped sink;
/// idle sampling never opens a telemetry file.
/// </summary>
public sealed class MetricCollector : IDisposable
{
    private readonly MonitoringOptions _options;
    private readonly SystemMetricProvider _system = new();
    private readonly List<IGpuMetricProvider> _gpuProviders = [];
    private readonly Action<MetricSample> _onSample;
    private readonly object _gate = new();

    private Process? _targetProcess;
    private MetricRecordingSession? _recording;
    private Task<MetricRecordingSummary>? _stopTask;
    private string? _activeSegment;
    private long _generation;
    private bool _running;
    private bool _disposed;

    public MetricCollector(MonitoringOptions options, Action<MetricSample> onSample)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(onSample);
        _options = options;
        _onSample = onSample;

        if (options.Gpu.Enabled)
        {
            InitializeGpuProviders(options.Gpu);
        }
    }

    public long SampleCount { get; private set; }
    public long OverrunCount { get; private set; }
    public IReadOnlyList<string> GpuProviders => _gpuProviders.Select(provider => provider.Name).ToArray();

    public Task? RecordingTask
    {
        get
        {
            lock (_gate)
            {
                return _recording?.Completion;
            }
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running)
            {
                throw new InvalidOperationException("The telemetry sampling loop is already running.");
            }

            _running = true;
        }

        var interval = TimeSpan.FromMilliseconds(_options.IntervalMs);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                Process? process;
                MetricRecordingSession? recording;
                string? segment;
                long generation;

                lock (_gate)
                {
                    process = _targetProcess;
                    recording = _recording;
                    segment = _activeSegment;
                    generation = _generation;
                }

                var sample = Collect(process);
                var duration = Stopwatch.GetElapsedTime(started);
                sample = sample with
                {
                    MeasurementSegment = segment,
                    CollectorDurationMs = duration.TotalMilliseconds,
                    CollectorDutyPercent = duration.TotalMilliseconds / _options.IntervalMs * 100.0,
                    Overrun = duration >= interval
                };

                lock (_gate)
                {
                    // A hardware read can overlap a start, stop, or segment
                    // boundary. Do not publish that sample into the new state.
                    if (generation == _generation)
                    {
                        SampleCount++;
                        if (sample.Overrun)
                        {
                            OverrunCount++;
                        }

                        recording?.Publish(sample);
                        _onSample(sample);
                    }
                }

                // Account for publication too. Collection duration remains a
                // provider wall-time measurement, not total runner CPU overhead.
                var remaining = interval - Stopwatch.GetElapsedTime(started);
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal lifetime shutdown; accepted rows still need to be drained.
        }
        finally
        {
            try
            {
                await StopRecordingAsync().ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    _running = false;
                }
            }
        }
    }

    public void StartRecording(Process process, string csvPath)
    {
        ArgumentNullException.ThrowIfNull(process);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_recording is not null || _stopTask is { IsCompleted: false })
            {
                throw new InvalidOperationException("The previous metric recording is still active or finalizing.");
            }

            var recording = new MetricRecordingSession(_options, csvPath);
            _targetProcess = process;
            _recording = recording;
            _activeSegment = null;
            _stopTask = null;
            _generation++;
        }
    }

    public void SetSegment(string? segment)
    {
        lock (_gate)
        {
            if (!string.Equals(_activeSegment, segment, StringComparison.Ordinal))
            {
                _activeSegment = segment;
                _generation++;
            }
        }
    }

    public Task<MetricRecordingSummary> StopRecordingAsync()
    {
        lock (_gate)
        {
            if (_recording is null)
            {
                return _stopTask ?? Task.FromResult(new MetricRecordingSummary(0, 0, 0, 0, 0, 0, GpuProviders));
            }

            var recording = _recording;
            _recording = null;
            _targetProcess = null;
            _activeSegment = null;
            _generation++;
            _stopTask = FinishRecordingAsync(recording, GpuProviders);
            return _stopTask;
        }
    }

    private static async Task<MetricRecordingSummary> FinishRecordingAsync(
        MetricRecordingSession recording, IReadOnlyList<string> gpuProviders)
    {
        await recording.StopAsync().ConfigureAwait(false);
        return recording.GetSummary(gpuProviders);
    }

    private MetricSample Collect(Process? process)
    {
        var system = _system.Sample(process, _options.ProcessIo);
        var gpu = new GpuSample();
        var errors = new List<string>(system.Errors);
        int? processId = null;

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    processId = process.Id;
                }
            }
            catch (InvalidOperationException)
            {
                // Process ownership may have ended while the sample was in flight.
            }
        }

        foreach (var provider in _gpuProviders)
        {
            try
            {
                gpu = gpu.Merge(provider.Sample(processId));
            }
            catch (Exception exception)
            {
                errors.Add($"{provider.Name}: {exception.Message}");
            }
        }

        return new MetricSample
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            HostCpuPercent = system.HostCpuPercent,
            ProcessCpuPercent = system.ProcessCpuPercent,
            HostMemoryTotalBytes = system.HostMemoryTotalBytes,
            HostMemoryUsedBytes = system.HostMemoryUsedBytes,
            HostMemoryAvailableBytes = system.HostMemoryAvailableBytes,
            ProcessWorkingSetBytes = system.ProcessWorkingSetBytes,
            ProcessPrivateBytes = system.ProcessPrivateBytes,
            SwapTotalBytes = system.SwapTotalBytes,
            SwapUsedBytes = system.SwapUsedBytes,
            PageFileUsagePercent = system.PageFileUsagePercent,
            ProcessReadBytesPerSecond = system.ProcessReadBytesPerSecond,
            ProcessWriteBytesPerSecond = system.ProcessWriteBytesPerSecond,
            GpuUtilizationPercent = gpu.UtilizationPercent,
            ProcessGpuUtilizationPercent = gpu.ProcessUtilizationPercent,
            VramTotalBytes = gpu.VramTotalBytes,
            VramUsedBytes = gpu.VramUsedBytes,
            ProcessVramBytes = gpu.ProcessVramBytes,
            GpuTemperatureC = gpu.TemperatureC,
            GpuPowerWatts = gpu.PowerWatts,
            Errors = errors
        };
    }

    private void InitializeGpuProviders(GpuOptions options)
    {
        var requested = options.Provider.Trim().ToLowerInvariant();
        if (requested is "auto" or "nvidia" or "nvml")
        {
            if (NvidiaNvmlProvider.TryCreate(options, out var nvml, out _) && nvml is not null)
            {
                _gpuProviders.Add(nvml);
            }
        }

        if (requested is "auto" or "windows")
        {
            if (WindowsGpuPerformanceCounterProvider.TryCreate(options, out var windows, out _) && windows is not null)
            {
                _gpuProviders.Add(windows);
            }
        }

        if (requested is "auto" or "linux" or "drm")
        {
            if (LinuxDrmProvider.TryCreate(options, out var drm, out _) && drm is not null)
            {
                _gpuProviders.Add(drm);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_running)
            {
                throw new InvalidOperationException("Stop and await the sampling loop before disposing its providers.");
            }

            _disposed = true;
        }

        try
        {
            StopRecordingAsync().GetAwaiter().GetResult();
        }
        finally
        {
            _system.Dispose();
            foreach (var provider in _gpuProviders)
            {
                provider.Dispose();
            }

            _gpuProviders.Clear();
        }
    }
}
