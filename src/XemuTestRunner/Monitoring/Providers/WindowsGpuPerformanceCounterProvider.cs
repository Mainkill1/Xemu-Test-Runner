using System.Diagnostics;
using XemuTestRunner.Config;

namespace XemuTestRunner.Monitoring.Providers;

#pragma warning disable CA1416
public sealed class WindowsGpuPerformanceCounterProvider : IGpuMetricProvider
{
    private readonly int _refreshMs;
    private readonly int _sampleIntervalMs;
    private readonly int _sensorIntervalMs;

    private int? _pid;
    private long _nextRefreshTick;
    private long _nextHostEngineSampleTick;
    private long _nextProcessEngineSampleTick;
    private long _nextMemorySampleTick;

    private readonly List<EngineCounter> _hostEngineCounters = [];
    private readonly List<EngineCounter> _processEngineCounters = [];
    private readonly List<PerformanceCounter> _dedicatedMemoryCounters = [];

    private double? _cachedHostUtilization;
    private double? _cachedProcessUtilization;
    private long? _cachedProcessVram;

    public string Name => "Windows GPU performance counters";

    private WindowsGpuPerformanceCounterProvider(
        int refreshMs,
        int sampleIntervalMs,
        int sensorIntervalMs)
    {
        _refreshMs = Math.Max(sampleIntervalMs, refreshMs);
        _sampleIntervalMs = Math.Max(100, sampleIntervalMs);
        _sensorIntervalMs = Math.Max(_sampleIntervalMs, sensorIntervalMs);
    }

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

            provider = new WindowsGpuPerformanceCounterProvider(
                options.CounterRefreshMs,
                options.SampleIntervalMs,
                options.SensorIntervalMs);

            status = instances.Length == 0
                ? "Windows GPU performance counters available, but no GPU engine instances are active yet."
                : $"Windows GPU performance counters available ({instances.Length} active instances).";
            return true;
        }
        catch (Exception ex)
        {
            status =
                $"Windows GPU performance counters unavailable: {ex.Message}";
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
            _processEngineCounters.Count == 0)
        {
            Refresh(processId);
            now = Environment.TickCount64;
        }

        if (_nextHostEngineSampleTick == 0 ||
            now >= _nextHostEngineSampleTick)
        {
            _nextHostEngineSampleTick = now + _sensorIntervalMs;
            _cachedHostUtilization =
                SampleGroupedMaximum(_hostEngineCounters);
        }

        if (processId is not null &&
            (_nextProcessEngineSampleTick == 0 ||
             now >= _nextProcessEngineSampleTick))
        {
            _nextProcessEngineSampleTick =
                now + _sampleIntervalMs;
            _cachedProcessUtilization =
                SampleGroupedMaximum(_processEngineCounters);
        }

        if (processId is not null &&
            (_nextMemorySampleTick == 0 ||
             now >= _nextMemorySampleTick))
        {
            _nextMemorySampleTick =
                now + _sensorIntervalMs;
            _cachedProcessVram =
                SampleDedicatedMemory();
        }

        return new GpuSample(
            UtilizationPercent: _cachedHostUtilization,
            ProcessUtilizationPercent:
                processId is null
                    ? null
                    : _cachedProcessUtilization,
            ProcessVramBytes:
                processId is null
                    ? null
                    : _cachedProcessVram);
    }

    private void Refresh(int? pid)
    {
        DisposeCounters();
        _pid = pid;

        string[] instances;
        try
        {
            instances = new PerformanceCounterCategory(
                "GPU Engine").GetInstanceNames();
        }
        catch
        {
            instances = [];
        }

        foreach (var instance in instances)
        {
            TryAddEngineCounter(
                _hostEngineCounters,
                instance);

            if (pid is not null &&
                BelongsToProcess(instance, pid.Value))
            {
                TryAddEngineCounter(
                    _processEngineCounters,
                    instance);
            }
        }

        if (pid is not null)
        {
            try
            {
                var memoryCategory =
                    new PerformanceCounterCategory(
                        "GPU Process Memory");

                foreach (var instance in
                         memoryCategory.GetInstanceNames())
                {
                    if (!BelongsToProcess(
                            instance,
                            pid.Value))
                        continue;

                    try
                    {
                        var counter =
                            new PerformanceCounter(
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
        }

        var hasProcessEngine =
            _processEngineCounters.Count > 0;

        _nextRefreshTick =
            Environment.TickCount64 +
            (pid is null
                ? _refreshMs
                : hasProcessEngine
                    ? _refreshMs
                    : 500);

        _nextHostEngineSampleTick = 0;
        _nextProcessEngineSampleTick = 0;
        _nextMemorySampleTick = 0;
    }

    private static void TryAddEngineCounter(
        List<EngineCounter> destination,
        string instance)
    {
        try
        {
            var counter = new PerformanceCounter(
                "GPU Engine",
                "Utilization Percentage",
                instance,
                readOnly: true);

            _ = counter.NextValue();
            destination.Add(
                new EngineCounter(
                    counter,
                    ExtractEngineKey(instance)));
        }
        catch
        {
        }
    }

    private long? SampleDedicatedMemory()
    {
        if (_dedicatedMemoryCounters.Count == 0)
            return null;

        try
        {
            var total = _dedicatedMemoryCounters.Sum(
                counter =>
                {
                    var value = counter.NextValue();
                    return double.IsFinite(value) &&
                           value > 0
                        ? value
                        : 0;
                });

            return checked((long)total);
        }
        catch
        {
            return null;
        }
    }

    private static double? SampleGroupedMaximum(
        IReadOnlyList<EngineCounter> counters)
    {
        if (counters.Count == 0)
            return null;

        var engines = new Dictionary<string, double>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var binding in counters)
        {
            double value;
            try
            {
                value = binding.Counter.NextValue();
            }
            catch
            {
                continue;
            }

            if (!double.IsFinite(value) || value < 0)
                continue;

            engines.TryGetValue(
                binding.EngineKey,
                out var existing);
            engines[binding.EngineKey] =
                existing + value;
        }

        return engines.Count == 0
            ? null
            : Math.Clamp(
                engines.Values.Max(),
                0,
                100);
    }

    private static bool BelongsToProcess(
        string instance,
        int pid) =>
        instance.StartsWith(
            $"pid_{pid}_",
            StringComparison.OrdinalIgnoreCase);

    private static string ExtractEngineKey(
        string instance)
    {
        var luid = instance.IndexOf(
            "_luid_",
            StringComparison.OrdinalIgnoreCase);

        return luid >= 0
            ? instance[luid..]
            : instance;
    }

    private void DisposeCounters()
    {
        foreach (var binding in _hostEngineCounters)
            binding.Counter.Dispose();

        foreach (var binding in _processEngineCounters)
            binding.Counter.Dispose();

        foreach (var counter in _dedicatedMemoryCounters)
            counter.Dispose();

        _hostEngineCounters.Clear();
        _processEngineCounters.Clear();
        _dedicatedMemoryCounters.Clear();

        _cachedHostUtilization = null;
        _cachedProcessUtilization = null;
        _cachedProcessVram = null;
    }

    public void Dispose() =>
        DisposeCounters();

    private sealed record EngineCounter(
        PerformanceCounter Counter,
        string EngineKey);
}
#pragma warning restore CA1416
