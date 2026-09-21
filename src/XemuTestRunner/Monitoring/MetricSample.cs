namespace XemuTestRunner.Monitoring;

public sealed record MetricSample
{
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public double? HostCpuPercent { get; init; }
    public double? ProcessCpuPercent { get; init; }
    public long? HostMemoryTotalBytes { get; init; }
    public long? HostMemoryUsedBytes { get; init; }
    public long? HostMemoryAvailableBytes { get; init; }
    public long? ProcessWorkingSetBytes { get; init; }
    public long? ProcessPrivateBytes { get; init; }
    public long? SwapTotalBytes { get; init; }
    public long? SwapUsedBytes { get; init; }
    public double? PageFileUsagePercent { get; init; }
    public double? ProcessReadBytesPerSecond { get; init; }
    public double? ProcessWriteBytesPerSecond { get; init; }
    public double? GpuUtilizationPercent { get; init; }
    public double? ProcessGpuUtilizationPercent { get; init; }
    public long? VramTotalBytes { get; init; }
    public long? VramUsedBytes { get; init; }
    public long? ProcessVramBytes { get; init; }
    public double? GpuTemperatureC { get; init; }
    public double? GpuPowerWatts { get; init; }
    public double CollectorDurationMs { get; init; }
    public bool Overrun { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
}

public sealed record GpuSample(
    double? UtilizationPercent = null,
    double? ProcessUtilizationPercent = null,
    long? VramTotalBytes = null,
    long? VramUsedBytes = null,
    long? ProcessVramBytes = null,
    double? TemperatureC = null,
    double? PowerWatts = null)
{
    public GpuSample Merge(GpuSample other) => new(
        UtilizationPercent ?? other.UtilizationPercent,
        ProcessUtilizationPercent ?? other.ProcessUtilizationPercent,
        VramTotalBytes ?? other.VramTotalBytes,
        VramUsedBytes ?? other.VramUsedBytes,
        ProcessVramBytes ?? other.ProcessVramBytes,
        TemperatureC ?? other.TemperatureC,
        PowerWatts ?? other.PowerWatts);
}
