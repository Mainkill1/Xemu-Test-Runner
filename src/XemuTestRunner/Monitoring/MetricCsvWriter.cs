using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace XemuTestRunner.Monitoring;

/// <summary>Formats and drains one run's CSV. It never polls hardware.</summary>
internal static class MetricCsvWriter
{
    private const string Header =
        "timestamp_utc,segment,host_cpu_pct,process_cpu_core_pct,host_mem_total," +
        "host_mem_used,host_mem_available,process_working_set,process_private," +
        "swap_total,swap_used,pagefile_usage_pct,process_read_bps,process_write_bps," +
        "gpu_pct,process_gpu_pct,vram_total,vram_used,process_vram,gpu_temp_c," +
        "gpu_power_w,collector_ms,collector_duty_pct,overrun,errors";

    public static async Task WriteAsync(
        ChannelReader<MetricSample> samples,
        FileStream file,
        int flushIntervalMs,
        Action rowWritten)
    {
        await using var writer = new StreamWriter(file, new UTF8Encoding(false), 256 * 1024);
        await writer.WriteLineAsync(Header).ConfigureAwait(false);
        var lastFlush = Stopwatch.GetTimestamp();

        // Completing the channel, rather than cancelling this reader, preserves
        // the samples accepted before StopRecordingAsync closed the recording.
        await foreach (var sample in samples.ReadAllAsync().ConfigureAwait(false))
        {
            await writer.WriteLineAsync(FormatRow(sample)).ConfigureAwait(false);
            rowWritten();

            if (Stopwatch.GetElapsedTime(lastFlush).TotalMilliseconds >= flushIntervalMs)
            {
                await writer.FlushAsync().ConfigureAwait(false);
                lastFlush = Stopwatch.GetTimestamp();
            }
        }

        await writer.FlushAsync().ConfigureAwait(false);
    }

    private static string FormatRow(MetricSample sample) => string.Join(',',
        sample.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
        Quote(sample.MeasurementSegment ?? ""),
        Number(sample.HostCpuPercent),
        Number(sample.ProcessCpuPercent),
        Bytes(sample.HostMemoryTotalBytes),
        Bytes(sample.HostMemoryUsedBytes),
        Bytes(sample.HostMemoryAvailableBytes),
        Bytes(sample.ProcessWorkingSetBytes),
        Bytes(sample.ProcessPrivateBytes),
        Bytes(sample.SwapTotalBytes),
        Bytes(sample.SwapUsedBytes),
        Number(sample.PageFileUsagePercent),
        Number(sample.ProcessReadBytesPerSecond),
        Number(sample.ProcessWriteBytesPerSecond),
        Number(sample.GpuUtilizationPercent),
        Number(sample.ProcessGpuUtilizationPercent),
        Bytes(sample.VramTotalBytes),
        Bytes(sample.VramUsedBytes),
        Bytes(sample.ProcessVramBytes),
        Number(sample.GpuTemperatureC),
        Number(sample.GpuPowerWatts),
        Number(sample.CollectorDurationMs),
        Number(sample.CollectorDutyPercent),
        sample.Overrun ? "1" : "0",
        Quote(string.Join(" | ", sample.Errors)));

    private static string Number(double? value) =>
        value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";

    private static string Bytes(long? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static string Quote(string value) =>
        "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
