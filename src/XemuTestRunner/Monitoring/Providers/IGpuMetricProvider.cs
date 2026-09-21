namespace XemuTestRunner.Monitoring.Providers;

public interface IGpuMetricProvider : IDisposable
{
    string Name { get; }
    GpuSample Sample(int? processId);
}
