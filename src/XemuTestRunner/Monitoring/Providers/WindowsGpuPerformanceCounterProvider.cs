using System.Diagnostics;
using XemuTestRunner.Config;

namespace XemuTestRunner.Monitoring.Providers;

#pragma warning disable CA1416
public sealed class WindowsGpuPerformanceCounterProvider : IGpuMetricProvider
{
    private readonly int _refreshMs;
    private int? _pid;
    private long _nextRefreshTick;
    private long _nextEngineSampleTick;
    private readonly List<EngineCounter> _engineCounters = [];
    private readonly List<PerformanceCounter> _dedicatedMemoryCounters = [];
    private double? _cachedHostUtilization;
    private double? _cachedProcessUtilization;

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
        var now = Environment.TickCount64;
        if (_nextRefreshTick == 0 ||
            _pid != processId ||
            now >= _nextRefreshTick ||
            processId is not null &&
            !_engineCounters.Any(counter => counter.IsTargetProcess))
            Refresh(processId);

        SampleEnginesIfDue();

        long? processVram = null;
        if (processId is not null)
        {
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
        }

        return new GpuSample(
            UtilizationPercent: _cachedHostUtilization,
            ProcessUtilizationPercent: processId is null ? null : _cachedProcessUtilization,
            ProcessVramBytes: processVram);
    }

    private void SampleEnginesIfDue()
    {
        var now = Environment.TickCount64;
        if (now < _nextEngineSampleTick)
            return;

        _nextEngineSampleTick = now + 250;

        if (_engineCounters.Count == 0)
        {
            _cachedHostUtilization = null;
            _cachedProcessUtilization = null;
            return;
        }

        var host = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var process = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var binding in _engineCounters)
        {
            double value;
            try { value = binding.Counter.NextValue(); }
            catch { continue; }

            if (!double.IsFinite(value) || value < 0)
                continue;

            host.TryGetValue(binding.EngineKey, out var hostCurrent);
            host[binding.EngineKey] = hostCurrent + value;

            if (binding.IsTargetProcess)
            {
                process.TryGetValue(binding.EngineKey, out var processCurrent);
                process[binding.EngineKey] = processCurrent + value;
            }
        }

        _cachedHostUtilization = host.Count == 0
            ? null
            : Math.Clamp(host.Values.Max(), 0, 100);
        _cachedProcessUtilization = process.Count == 0
            ? null
            : Math.Clamp(process.Values.Max(), 0, 100);
    }

    private void Refresh(int? pid)
    {
        DisposeCounters();
        _pid = pid;

        try
        {
            var engineCategory = new PerformanceCounterCategory("GPU Engine");
            foreach (var instance in engineCategory.GetInstanceNames())
            {
                try
                {
                    var counter = new PerformanceCounter(
                        "GPU Engine",
                        "Utilization Percentage",
                        instance,
                        readOnly: true);
                    _ = counter.NextValue();
                    _engineCounters.Add(new EngineCounter(
                        counter,
                        ExtractEngineKey(instance),
                        pid is not null && BelongsToProcess(instance, pid.Value)));
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
                if (pid is null || !BelongsToProcess(instance, pid.Value))
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

        var hasProcessEngine = _engineCounters.Any(counter => counter.IsTargetProcess);
        _nextRefreshTick = Environment.TickCount64 +
            (pid is null ? _refreshMs : hasProcessEngine ? _refreshMs : 500);
        _nextEngineSampleTick = 0;
    }

    private static bool BelongsToProcess(string instance, int pid) =>
        instance.StartsWith($"pid_{pid}_", StringComparison.OrdinalIgnoreCase);

    private static string ExtractEngineKey(string instance)
    {
        var luid = instance.IndexOf("_luid_", StringComparison.OrdinalIgnoreCase);
        return luid >= 0 ? instance[luid..] : instance;
    }

    private void DisposeCounters()
    {
        foreach (var binding in _engineCounters)
            binding.Counter.Dispose();
        foreach (var counter in _dedicatedMemoryCounters)
            counter.Dispose();

        _engineCounters.Clear();
        _dedicatedMemoryCounters.Clear();
        _cachedHostUtilization = null;
        _cachedProcessUtilization = null;
    }

    public void Dispose() => DisposeCounters();

    private sealed record EngineCounter(
        PerformanceCounter Counter,
        string EngineKey,
        bool IsTargetProcess);
}
#pragma warning restore CA1416
