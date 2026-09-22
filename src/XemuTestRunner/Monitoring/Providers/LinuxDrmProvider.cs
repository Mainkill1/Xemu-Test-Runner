using System.Globalization;
using XemuTestRunner.Config;

namespace XemuTestRunner.Monitoring.Providers;

public sealed class LinuxDrmProvider : IGpuMetricProvider
{
    private readonly string _devicePath;
    private readonly int _sampleIntervalMs;
    private readonly int _sensorIntervalMs;
    private long _nextFastSampleTick;
    private long _nextSensorSampleTick;
    private double? _cachedUtilization;
    private long? _cachedVramTotal;
    private long? _cachedVramUsed;

    public string Name => "Linux DRM sysfs";

    private LinuxDrmProvider(
        string devicePath,
        int sampleIntervalMs,
        int sensorIntervalMs)
    {
        _devicePath = devicePath;
        _sampleIntervalMs = sampleIntervalMs;
        _sensorIntervalMs = sensorIntervalMs;
    }

    public static bool TryCreate(GpuOptions options, out LinuxDrmProvider? provider, out string status)
    {
        provider = null;
        status = "Linux DRM telemetry unavailable.";
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            var cards = Directory.EnumerateDirectories("/sys/class/drm", "card*")
                .Where(path => int.TryParse(Path.GetFileName(path).AsSpan(4), out _))
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
            if (options.DeviceIndex < 0 || options.DeviceIndex >= cards.Length)
            {
                status = $"DRM GPU index {options.DeviceIndex} is unavailable.";
                return false;
            }
            var devicePath = Path.Combine(cards[options.DeviceIndex], "device");
            if (!Directory.Exists(devicePath)) return false;
            provider = new LinuxDrmProvider(
                devicePath,
                options.SampleIntervalMs,
                options.SensorIntervalMs);
            status = $"DRM GPU index {options.DeviceIndex} available.";
            return true;
        }
        catch (Exception ex)
        {
            status = $"DRM telemetry unavailable: {ex.Message}";
            return false;
        }
    }

    public GpuSample Sample(int? processId)
    {
        var now = Environment.TickCount64;

        if (_nextFastSampleTick == 0 || now >= _nextFastSampleTick)
        {
            _nextFastSampleTick = now + _sampleIntervalMs;
            _cachedUtilization = ReadDouble("gpu_busy_percent");
        }

        if (_nextSensorSampleTick == 0 || now >= _nextSensorSampleTick)
        {
            _nextSensorSampleTick = now + _sensorIntervalMs;
            _cachedVramTotal = ReadLong("mem_info_vram_total");
            _cachedVramUsed = ReadLong("mem_info_vram_used");
        }

        return new GpuSample(
            UtilizationPercent: _cachedUtilization,
            VramTotalBytes: _cachedVramTotal,
            VramUsedBytes: _cachedVramUsed);
    }

    private double? ReadDouble(string name)
    {
        try
        {
            var text = File.ReadAllText(Path.Combine(_devicePath, name)).Trim();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
        }
        catch { return null; }
    }

    private long? ReadLong(string name)
    {
        try
        {
            var text = File.ReadAllText(Path.Combine(_devicePath, name)).Trim();
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
        }
        catch { return null; }
    }

    public void Dispose() { }
}
