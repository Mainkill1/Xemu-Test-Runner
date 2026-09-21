using System.Diagnostics;
using XemuTestRunner.Config;

namespace XemuTestRunner.Monitoring.Providers;

#pragma warning disable CA1416 // Private construction is gated by the Windows check in TryCreate.
public sealed class WindowsGpuPerformanceCounterProvider : IGpuMetricProvider
{
    private readonly int _refreshMs;
    private int? _pid;
    private long _nextRefreshTick;
    private readonly List<PerformanceCounter> _engineCounters = [];
    private readonly List<PerformanceCounter> _dedicatedMemoryCounters = [];
    public string Name => "Windows GPU performance counters";
    private WindowsGpuPerformanceCounterProvider(int refreshMs) => _refreshMs = refreshMs;

    public static bool TryCreate(GpuOptions options, out WindowsGpuPerformanceCounterProvider? provider, out string status)
    {
        provider = null;
        status = "Windows GPU performance counters unavailable.";
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            _ = new PerformanceCounterCategory("GPU Engine").GetInstanceNames();
            provider = new WindowsGpuPerformanceCounterProvider(options.CounterRefreshMs);
            status = "Windows GPU performance counters available.";
            return true;
        }
        catch (Exception ex)
        {
            status = $"Windows GPU performance counters unavailable: {ex.Message}";
            return false;
        }
    }

    public GpuSample Sample(int? processId)
    {
        if (processId is null) return new GpuSample();
        if (_pid != processId || Environment.TickCount64 >= _nextRefreshTick) Refresh(processId.Value);
        double? processUtilization = null;
        long? processVram = null;
        try
        {
            if (_engineCounters.Count > 0) processUtilization = Math.Clamp(_engineCounters.Max(counter => (double)counter.NextValue()), 0, 100);
        }
        catch { }
        try
        {
            if (_dedicatedMemoryCounters.Count > 0) processVram = checked((long)_dedicatedMemoryCounters.Sum(counter => (double)counter.NextValue()));
        }
        catch { }
        return new GpuSample(ProcessUtilizationPercent: processUtilization, ProcessVramBytes: processVram);
    }

    private void Refresh(int pid)
    {
        DisposeCounters();
        _pid = pid;
        _nextRefreshTick = Environment.TickCount64 + Math.Max(250, _refreshMs);
        var needle = $"pid_{pid}_";
        try
        {
            var engineCategory = new PerformanceCounterCategory("GPU Engine");
            foreach (var instance in engineCategory.GetInstanceNames().Where(name => name.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            {
                var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, readOnly: true);
                _ = counter.NextValue();
                _engineCounters.Add(counter);
            }
        }
        catch { }
        try
        {
            var memoryCategory = new PerformanceCounterCategory("GPU Process Memory");
            foreach (var instance in memoryCategory.GetInstanceNames().Where(name => name.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            {
                var counter = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", instance, readOnly: true);
                _ = counter.NextValue();
                _dedicatedMemoryCounters.Add(counter);
            }
        }
        catch { }
    }

    private void DisposeCounters()
    {
        foreach (var counter in _engineCounters) counter.Dispose();
        foreach (var counter in _dedicatedMemoryCounters) counter.Dispose();
        _engineCounters.Clear();
        _dedicatedMemoryCounters.Clear();
    }

    public void Dispose() => DisposeCounters();
}
#pragma warning restore CA1416
