using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using XemuTestRunner.Config;
using XemuTestRunner.Monitoring.Providers;

namespace XemuTestRunner.Monitoring;

public sealed record MetricRecordingSummary(
    long Samples,
    long Overruns,
    long DroppedWriteSamples,
    IReadOnlyList<string> GpuProviders);

public sealed class MetricCollector : IDisposable
{
    private readonly MonitoringOptions _options;
    private readonly SystemMetricProvider _system = new();
    private readonly List<IGpuMetricProvider> _gpuProviders = [];
    private readonly Action<MetricSample> _onSample;
    private readonly object _gate = new();

    private Process? _targetProcess;
    private RecordingSession? _recording;
    private bool _disposed;

    public long SampleCount { get; private set; }
    public long OverrunCount { get; private set; }
    public IReadOnlyList<string> GpuProviders =>
        _gpuProviders.Select(provider => provider.Name).ToArray();

    public MetricCollector(
        MonitoringOptions options,
        Action<MetricSample> onSample)
    {
        _options = options;
        _onSample = onSample;

        if (options.Gpu.Enabled)
            InitializeGpuProviders(options.Gpu);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMilliseconds(_options.IntervalMs);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                Process? process;
                RecordingSession? recording;

                lock (_gate)
                {
                    process = _targetProcess;
                    recording = _recording;
                }

                MetricSample sample;
                try
                {
                    sample = Collect(process);
                }
                catch (Exception ex)
                {
                    sample = new MetricSample
                    {
                        TimestampUtc = DateTimeOffset.UtcNow,
                        Errors = ["collector: " + ex.Message]
                    };
                }

                var duration = Stopwatch.GetElapsedTime(started);
                var duty = _options.IntervalMs <= 0
                    ? 0
                    : duration.TotalMilliseconds / _options.IntervalMs * 100.0;

                sample = sample with
                {
                    CollectorDurationMs = duration.TotalMilliseconds,
                    CollectorDutyPercent = duty,
                    Overrun = duration >= interval
                };

                SampleCount++;
                if (sample.Overrun)
                    OverrunCount++;

                _onSample(sample);
                recording?.Publish(sample);

                var remaining = interval - duration;
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            RecordingSession? recording;
            lock (_gate)
            {
                recording = _recording;
                _recording = null;
                _targetProcess = null;
            }

            if (recording is not null)
                await recording.StopAsync().ConfigureAwait(false);
        }
    }

    public void StartRecording(Process process, string csvPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_recording is not null)
                throw new InvalidOperationException(
                    "Metric recording is already active.");

            _targetProcess = process;
            _recording = new RecordingSession(_options, csvPath);
        }
    }

    public async Task<MetricRecordingSummary> StopRecordingAsync()
    {
        RecordingSession? recording;

        lock (_gate)
        {
            recording = _recording;
            _recording = null;
            _targetProcess = null;
        }

        if (recording is null)
        {
            return new MetricRecordingSummary(
                0,
                0,
                0,
                GpuProviders);
        }

        await recording.StopAsync().ConfigureAwait(false);
        return new MetricRecordingSummary(
            recording.Samples,
            recording.Overruns,
            recording.DroppedWriteSamples,
            GpuProviders);
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
                    processId = process.Id;
            }
            catch (InvalidOperationException)
            {
            }
        }

        foreach (var provider in _gpuProviders)
        {
            try
            {
                gpu = gpu.Merge(provider.Sample(processId));
            }
            catch (Exception ex)
            {
                errors.Add($"{provider.Name}: {ex.Message}");
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
            if (NvidiaNvmlProvider.TryCreate(
                    options,
                    out var nvml,
                    out _) &&
                nvml is not null)
                _gpuProviders.Add(nvml);
        }

        if (requested is "auto" or "windows")
        {
            if (WindowsGpuPerformanceCounterProvider.TryCreate(
                    options,
                    out var windows,
                    out _) &&
                windows is not null)
                _gpuProviders.Add(windows);
        }

        if (requested is "auto" or "linux" or "drm")
        {
            if (LinuxDrmProvider.TryCreate(
                    options,
                    out var drm,
                    out _) &&
                drm is not null)
                _gpuProviders.Add(drm);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        RecordingSession? recording;
        lock (_gate)
        {
            recording = _recording;
            _recording = null;
            _targetProcess = null;
        }
        recording?.Dispose();

        _system.Dispose();
        foreach (var provider in _gpuProviders)
            provider.Dispose();
        _gpuProviders.Clear();
    }

    private sealed class RecordingSession : IDisposable
    {
        private readonly MonitoringOptions _options;
        private readonly Channel<MetricSample> _channel;
        private readonly Task _writerTask;
        private int _stopped;

        public long Samples { get; private set; }
        public long Overruns { get; private set; }
        public long DroppedWriteSamples { get; private set; }

        public RecordingSession(
            MonitoringOptions options,
            string csvPath)
        {
            _options = options;
            Directory.CreateDirectory(Path.GetDirectoryName(csvPath)!);

            _channel = Channel.CreateBounded<MetricSample>(
                new BoundedChannelOptions(options.BufferCapacity)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = BoundedChannelFullMode.Wait
                });

            _writerTask = WriteCsvAsync(
                _channel.Reader,
                csvPath,
                options.FlushIntervalMs);
        }

        public void Publish(MetricSample sample)
        {
            if (Volatile.Read(ref _stopped) != 0)
                return;

            Samples++;
            if (sample.Overrun)
                Overruns++;

            if (!_channel.Writer.TryWrite(sample))
                DroppedWriteSamples++;
        }

        public async Task StopAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
                return;

            _channel.Writer.TryComplete();
            await _writerTask.ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0)
                _channel.Writer.TryComplete();
        }

        private static async Task WriteCsvAsync(
            ChannelReader<MetricSample> reader,
            string path,
            int flushIntervalMs)
        {
            await using var file = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var writer = new StreamWriter(
                file,
                new System.Text.UTF8Encoding(false),
                256 * 1024,
                leaveOpen: false);

            await writer.WriteLineAsync(
                "timestamp_utc,host_cpu_pct,process_cpu_core_pct,host_mem_total,host_mem_used,host_mem_available,process_working_set,process_private,swap_total,swap_used,pagefile_usage_pct,process_read_bps,process_write_bps,gpu_pct,process_gpu_pct,vram_total,vram_used,process_vram,gpu_temp_c,gpu_power_w,collector_ms,collector_duty_pct,overrun,errors")
                .ConfigureAwait(false);

            var lastFlush = Stopwatch.GetTimestamp();
            await foreach (var sample in reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                await writer.WriteLineAsync(ToCsv(sample)).ConfigureAwait(false);

                if (Stopwatch.GetElapsedTime(lastFlush).TotalMilliseconds >= flushIntervalMs)
                {
                    await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    lastFlush = Stopwatch.GetTimestamp();
                }
            }

            await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }

        private static string ToCsv(MetricSample s)
        {
            static string D(double? value) =>
                value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
            static string L(long? value) =>
                value?.ToString(CultureInfo.InvariantCulture) ?? "";
            static string Q(string value) =>
                "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

            return string.Join(',',
                s.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
                D(s.HostCpuPercent),
                D(s.ProcessCpuPercent),
                L(s.HostMemoryTotalBytes),
                L(s.HostMemoryUsedBytes),
                L(s.HostMemoryAvailableBytes),
                L(s.ProcessWorkingSetBytes),
                L(s.ProcessPrivateBytes),
                L(s.SwapTotalBytes),
                L(s.SwapUsedBytes),
                D(s.PageFileUsagePercent),
                D(s.ProcessReadBytesPerSecond),
                D(s.ProcessWriteBytesPerSecond),
                D(s.GpuUtilizationPercent),
                D(s.ProcessGpuUtilizationPercent),
                L(s.VramTotalBytes),
                L(s.VramUsedBytes),
                L(s.ProcessVramBytes),
                D(s.GpuTemperatureC),
                D(s.GpuPowerWatts),
                s.CollectorDurationMs.ToString("0.###", CultureInfo.InvariantCulture),
                s.CollectorDutyPercent.ToString("0.###", CultureInfo.InvariantCulture),
                s.Overrun ? "1" : "0",
                Q(string.Join(" | ", s.Errors)));
        }
    }
}
