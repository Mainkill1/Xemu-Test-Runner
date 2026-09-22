namespace XemuTestRunner.Monitoring;

public sealed record MetricRecordingSummary(
    long Samples,
    long Overruns,
    long DroppedWriteSamples,
    double AverageCollectorDutyPercent,
    double MaxCollectorDutyPercent,
    double MaxCollectorDurationMs,
    IReadOnlyList<string> GpuProviders)
{
    // Samples counts rows actually written, not rows offered to a full queue.
    public long ObservedSamples { get; init; }
}
