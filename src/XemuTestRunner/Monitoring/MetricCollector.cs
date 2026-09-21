using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using XemuTestRunner.Config;
using XemuTestRunner.Monitoring.Providers;

namespace XemuTestRunner.Monitoring;

public sealed class MetricCollector : IDisposable
{
    private readonly MonitoringOptions _options;
    private readonly SystemMetricProvider _system = new();
    private readonly List<IGpuMetricProvider> _gpuProviders = [];
    private readonly Action<MetricSample> _onSample;
    private bool _disposed;

    public long SampleCount { get; private set; }
    public long OverrunCount { get; private set; }
    public long DroppedWriteSamples { get; private set; }
    public IReadOnlyList<string> GpuProviders => _gpuProviders.Select(provider => provider.Name).ToArray();

    public MetricCollector(MonitoringOptions options, Action<MetricSample> onSample)
    {
        _options = options;
        _onSample = onSample;
        if (options.Gpu.Enabled)
            InitializeGpuProviders(options.Gpu);
    }

    public async Task RunAsync(
        Process process,
        string csvPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(csvPath)!);

        var channel = Channel.CreateBounded<MetricSample>(
            new BoundedChannelOptions(_options.BufferCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });

        var writerTask = WriteCsvAsync(channel.Reader, csvPath, cancellationToken);
        var interval = TimeSpan.FromMilliseconds(_options.IntervalMs);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
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
                sample = sample with
                {
                    CollectorDurationMs = duration.TotalMilliseconds,
                    Overrun = duration >= interval
                };

                SampleCount++;
                if (sample.Overrun)
                    OverrunCount++;

                _onSample(sample);

                if (!channel.Writer.TryWrite(sample))
                    DroppedWriteSamples++;

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
            channel.Writer.TryComplete();
            try { await writerTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private MetricSample Collect(Process process)
    {
        var system = _system.Sample(process, _options.ProcessIo);
        var gpu = new GpuSample();
        var errors = new List<string>(system.Errors);

        foreach (var provider in _gpuProviders)
        {
            try
            {
                gpu = gpu.Merge(provider.Sample(process.HasExited ? null : process.Id));
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
            if (NvidiaNvmlProvider.TryCreate(options, out var nvml, out _) &&
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
            if (LinuxDrmProvider.TryCreate(options, out var drm, out _) &&
                drm is not null)
                _gpuProviders.Add(drm);
        }
    }

    private async Task WriteCsvAsync(
        ChannelReader<MetricSample> reader,
        string path,
        CancellationToken cancellationToken)
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
            "timestamp_utc,host_cpu_pct,process_cpu_core_pct,host_mem_total,host_mem_used,host_mem_available,process_working_set,process_private,swap_total,swap_used,pagefile_usage_pct,process_read_bps,process_write_bps,gpu_pct,process_gpu_pct,vram_total,vram_used,process_vram,gpu_temp_c,gpu_power_w,collector_ms,overrun,errors")
            .ConfigureAwait(false);

        var lastFlush = Stopwatch.GetTimestamp();
        await foreach (var sample in reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            await writer.WriteLineAsync(ToCsv(sample)).ConfigureAwait(false);
            if (Stopwatch.GetElapsedTime(lastFlush).TotalMilliseconds >= _options.FlushIntervalMs)
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
            s.Overrun ? "1" : "0",
            Q(string.Join(" | ", s.Errors)));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _system.Dispose();
        foreach (var provider in _gpuProviders)
            provider.Dispose();
        _gpuProviders.Clear();
    }
}
