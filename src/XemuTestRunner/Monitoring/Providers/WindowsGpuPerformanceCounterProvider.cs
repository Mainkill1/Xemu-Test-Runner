using System.Diagnostics;
using XemuTestRunner.Config;

namespace XemuTestRunner.Monitoring.Providers;

#pragma warning disable CA1416
public sealed class WindowsGpuPerformanceCounterProvider : IGpuMetricProvider
{
    private readonly int _refreshMs;
    private int? _pid;
    private long _nextRefreshTick;
    private long _nextHostSampleTick;
    private readonly List<EngineCounter> _processEngineCounters = [];
    private readonly List<EngineCounter> _hostEngineCounters = [];
    private readonly List<PerformanceCounter> _dedicatedMemoryCounters = [];
    private double? _cachedHostUtilization;

    public string Name => "Windows GPU performance counters";

    private WindowsGpuPerformanceCounterProvider(int refreshMs) =>
        _refreshMs = Math.Max(250, refreshMs);

    public static bool TryCreate(
        GpuOptions options,
        out WindowsGpuPerformanceCounterProvider? provider,
        out string status)
    {
        provider = null;
        status = "Windows GPU performance counters unavailable.";

        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            var instances = category.GetInstanceNames();
            provider = new WindowsGpuPerformanceCounterProvider(options.CounterRefreshMs);
            status = instances.Length == 0
                ? "Windows GPU performance counters available, but no GPU engine instances are active yet."
                : $"Windows GPU performance counters available ({instances.Length} active instances).";
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
        if (processId is null)
            return SampleHostOnly();

        if (_pid != processId ||
            Environment.TickCount64 >= _nextRefreshTick ||
            _processEngineCounters.Count == 0)
            Refresh(processId.Value);

        var processUtilization = SampleGroupedMaximum(_processEngineCounters);

        long? processVram = null;
        try
        {
            if (_dedicatedMemoryCounters.Count > 0)
            {
                var total = _dedicatedMemoryCounters.Sum(counter =>
                {
                    var value = counter.NextValue();
                    return double.IsFinite(value) && value > 0 ? value : 0;
                });
                processVram = checked((long)total);
            }
        }
        catch
        {
            processVram = null;
        }

        var hostUtilization = SampleHostCached();

        return new GpuSample(
            UtilizationPercent: hostUtilization,
            ProcessUtilizationPercent: processUtilization,
            ProcessVramBytes: processVram);
    }

    private GpuSample SampleHostOnly() =>
        new(UtilizationPercent: SampleHostCached());

    private double? SampleHostCached()
    {
        var now = Environment.TickCount64;
        if (now < _nextHostSampleTick)
            return _cachedHostUtilization;

        _nextHostSampleTick = now + 500;
        _cachedHostUtilization = SampleGroupedMaximum(_hostEngineCounters);
        return _cachedHostUtilization;
    }

    private void Refresh(int pid)
    {
        DisposeCounters();
        _pid = pid;

        try
        {
            var engineCategory = new PerformanceCounterCategory("GPU Engine");
            var instances = engineCategory.GetInstanceNames();

            foreach (var instance in instances)
            {
                try
                {
                    var counter = new PerformanceCounter(
                        "GPU Engine",
                        "Utilization Percentage",
                        instance,
                        readOnly: true);
                    _ = counter.NextValue();

                    var binding = new EngineCounter(
                        counter,
                        ExtractEngineKey(instance));

                    _hostEngineCounters.Add(binding);
                    if (BelongsToProcess(instance, pid))
                        _processEngineCounters.Add(binding);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        try
        {
            var memoryCategory = new PerformanceCounterCategory("GPU Process Memory");
            foreach (var instance in memoryCategory.GetInstanceNames())
            {
                if (!BelongsToProcess(instance, pid))
                    continue;

                try
                {
                    var counter = new PerformanceCounter(
                        "GPU Process Memory",
                        "Dedicated Usage",
                        instance,
                        readOnly: true);
                    _ = counter.NextValue();
                    _dedicatedMemoryCounters.Add(counter);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        _nextRefreshTick = Environment.TickCount64 +
            (_processEngineCounters.Count == 0 ? 500 : _refreshMs);
        _nextHostSampleTick = 0;
    }

    private static bool BelongsToProcess(string instance, int pid) =>
        instance.StartsWith($"pid_{pid}_", StringComparison.OrdinalIgnoreCase);

    private static string ExtractEngineKey(string instance)
    {
        var luid = instance.IndexOf("_luid_", StringComparison.OrdinalIgnoreCase);
        return luid >= 0 ? instance[luid..] : instance;
    }

    private static double? SampleGroupedMaximum(List<EngineCounter> counters)
    {
        if (counters.Count == 0)
            return null;

        try
        {
            var byEngine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var binding in counters)
            {
                var value = binding.Counter.NextValue();
                if (!double.IsFinite(value) || value < 0)
                    continue;

                byEngine.TryGetValue(binding.EngineKey, out var existing);
                byEngine[binding.EngineKey] = existing + value;
            }

            return byEngine.Count == 0
                ? null
                : Math.Clamp(byEngine.Values.Max(), 0, 100);
        }
        catch
        {
            return null;
        }
    }

    private void DisposeCounters()
    {
        var disposed = new HashSet<PerformanceCounter>();

        foreach (var binding in _hostEngineCounters)
        {
            if (disposed.Add(binding.Counter))
                binding.Counter.Dispose();
        }

        foreach (var counter in _dedicatedMemoryCounters)
        {
            if (disposed.Add(counter))
                counter.Dispose();
        }

        _processEngineCounters.Clear();
        _hostEngineCounters.Clear();
        _dedicatedMemoryCounters.Clear();
    }

    public void Dispose() => DisposeCounters();

    private sealed record EngineCounter(
        PerformanceCounter Counter,
        string EngineKey);
}
#pragma warning restore CA1416
