using System.Diagnostics;
using XemuTestRunner.Config;

namespace XemuTestRunner.Monitoring.Providers;

#pragma warning disable CA1416
public sealed class WindowsGpuPerformanceCounterProvider : IGpuMetricProvider
{
    private const string EngineCategory = "GPU Engine";
    private const string MemoryCategory = "GPU Process Memory";
    private readonly int _discoveryIntervalMs;
    private readonly int _processIntervalMs;
    private readonly int _sensorIntervalMs;
    private readonly Dictionary<string, PerformanceCounter> _hostEngines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PerformanceCounter> _processEngines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PerformanceCounter> _processMemory = new(StringComparer.OrdinalIgnoreCase);

    private int? _processId;
    private int _emptyDiscoveries;
    private long _nextDiscovery;
    private long _nextHostSample;
    private long _nextProcessSample;
    private long _nextMemorySample;
    private double? _hostUtilization;
    private double? _processUtilization;
    private long? _processVram;

    private WindowsGpuPerformanceCounterProvider(GpuOptions options)
    {
        _processIntervalMs = Math.Max(100, options.SampleIntervalMs);
        _sensorIntervalMs = Math.Max(_processIntervalMs, options.SensorIntervalMs);
        _discoveryIntervalMs = Math.Max(_processIntervalMs, options.CounterRefreshMs);
    }

    public string Name => "Windows GPU performance counters";

    public static bool TryCreate(
        GpuOptions options, out WindowsGpuPerformanceCounterProvider? provider, out string status)
    {
        provider = null;
        status = "Windows GPU performance counters unavailable.";
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var instances = new PerformanceCounterCategory(EngineCategory).GetInstanceNames();
            provider = new WindowsGpuPerformanceCounterProvider(options);
            status = $"Windows GPU performance counters available ({instances.Length} active instances).";
            return true;
        }
        catch (Exception exception)
        {
            status = $"Windows GPU performance counters unavailable: {exception.Message}";
            return false;
        }
    }

    public GpuSample Sample(int? processId)
    {
        var now = Environment.TickCount64;
        if (_processId != processId)
        {
            ChangeTarget(processId);
        }

        // A missing engine is not permission to enumerate counters every poll.
        // Discovery owns its deadline, including retries while xemu starts up.
        if (_nextDiscovery == 0 || now >= _nextDiscovery)
        {
            DiscoverCounters();
            now = Environment.TickCount64;
        }

        if (now >= _nextHostSample)
        {
            _hostUtilization = ReadBusiestEngine(_hostEngines);
            _nextHostSample = now + _sensorIntervalMs;
        }

        if (processId is not null && now >= _nextProcessSample)
        {
            _processUtilization = ReadBusiestEngine(_processEngines);
            _nextProcessSample = now + _processIntervalMs;
        }

        if (processId is not null && now >= _nextMemorySample)
        {
            _processVram = ReadDedicatedMemory();
            _nextMemorySample = now + _sensorIntervalMs;
        }

        return new GpuSample(
            UtilizationPercent: _hostUtilization,
            ProcessUtilizationPercent: processId is null ? null : _processUtilization,
            ProcessVramBytes: processId is null ? null : _processVram);
    }

    private void ChangeTarget(int? processId)
    {
        _processId = processId;
        _processUtilization = null;
        _processVram = null;
        _emptyDiscoveries = 0;
        _nextDiscovery = 0;
        DisposeCounters(_processEngines);
        DisposeCounters(_processMemory);
    }

    private void DiscoverCounters()
    {
        var engineInstances = TryGetInstances(EngineCategory);
        if (engineInstances is not null)
        {
            SynchronizeCounters(_hostEngines, EngineCategory, "Utilization Percentage", engineInstances);
            var targetInstances = engineInstances.Where(BelongsToTarget).ToArray();
            SynchronizeCounters(_processEngines, EngineCategory, "Utilization Percentage", targetInstances);
        }

        if (_processId is not null)
        {
            var memoryInstances = TryGetInstances(MemoryCategory);
            if (memoryInstances is not null)
            {
                SynchronizeCounters(_processMemory, MemoryCategory, "Dedicated Usage",
                    memoryInstances.Where(BelongsToTarget).ToArray());
            }
        }

        var now = Environment.TickCount64;
        var retryDelay = _discoveryIntervalMs;
        if (_processId is not null && _processEngines.Count == 0)
        {
            // Try promptly for a new process, then back off to normal discovery.
            retryDelay = Math.Min(_discoveryIntervalMs, 500 * (1 << Math.Min(_emptyDiscoveries, 4)));
            _emptyDiscoveries++;
        }
        else
        {
            _emptyDiscoveries = 0;
        }

        _nextDiscovery = now + retryDelay;
        if (_nextHostSample == 0)
        {
            _nextHostSample = now + _sensorIntervalMs;
        }

        if (_nextProcessSample == 0)
        {
            _nextProcessSample = now + _processIntervalMs;
        }
    }

    private bool BelongsToTarget(string instance) =>
        _processId is not null && instance.StartsWith($"pid_{_processId}_", StringComparison.OrdinalIgnoreCase);

    private static string[]? TryGetInstances(string category)
    {
        try
        {
            return new PerformanceCounterCategory(category).GetInstanceNames();
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            return null; // Keep live counters; an unsuccessful discovery is not an empty snapshot.
        }
    }

    private static void SynchronizeCounters(
        Dictionary<string, PerformanceCounter> counters,
        string category,
        string counterName,
        IReadOnlyCollection<string> desiredInstances)
    {
        var desired = new HashSet<string>(desiredInstances, StringComparer.OrdinalIgnoreCase);
        foreach (var obsolete in counters.Keys.Where(name => !desired.Contains(name)).ToArray())
        {
            counters[obsolete].Dispose();
            counters.Remove(obsolete);
        }

        foreach (var instance in desired)
        {
            if (counters.ContainsKey(instance))
            {
                continue; // Preserve rate-counter baselines across discovery.
            }

            PerformanceCounter? counter = null;
            try
            {
                counter = new PerformanceCounter(category, counterName, instance, readOnly: true);
                _ = counter.NextValue();
                counters.Add(instance, counter);
            }
            catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
            {
                counter?.Dispose(); // Instances may disappear between discovery and opening.
            }
        }
    }

    private static double? ReadBusiestEngine(IReadOnlyDictionary<string, PerformanceCounter> counters)
    {
        var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, counter) in counters)
        {
            var value = TryRead(counter);
            if (value is null)
            {
                continue;
            }

            var luidOffset = instance.IndexOf("_luid_", StringComparison.OrdinalIgnoreCase);
            var engine = luidOffset >= 0 ? instance[luidOffset..] : instance;
            engines.TryGetValue(engine, out var current);
            engines[engine] = current + value.Value;
        }

        return engines.Count == 0 ? null : Math.Clamp(engines.Values.Max(), 0, 100);
    }

    private long? ReadDedicatedMemory()
    {
        if (_processMemory.Count == 0)
        {
            return null;
        }

        double total = 0;
        foreach (var counter in _processMemory.Values)
        {
            var value = TryRead(counter);
            if (value is null)
            {
                return null; // A partial sum must not masquerade as total process VRAM.
            }

            total += value.Value;
        }

        return total <= long.MaxValue ? (long)total : null;
    }

    private static double? TryRead(PerformanceCounter counter)
    {
        try
        {
            var value = counter.NextValue();
            return double.IsFinite(value) && value >= 0 ? value : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        DisposeCounters(_hostEngines);
        DisposeCounters(_processEngines);
        DisposeCounters(_processMemory);
    }

    private static void DisposeCounters(Dictionary<string, PerformanceCounter> counters)
    {
        foreach (var counter in counters.Values)
        {
            counter.Dispose();
        }

        counters.Clear();
    }
}
#pragma warning restore CA1416
