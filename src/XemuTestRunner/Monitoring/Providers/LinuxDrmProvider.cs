using System.Globalization;
using XemuTestRunner.Config;

namespace XemuTestRunner.Monitoring.Providers;

public sealed class LinuxDrmProvider : IGpuMetricProvider
{
    private readonly string _devicePath;
    public string Name => "Linux DRM sysfs";
    private LinuxDrmProvider(string devicePath) => _devicePath = devicePath;

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
            provider = new LinuxDrmProvider(devicePath);
            status = $"DRM GPU index {options.DeviceIndex} available.";
            return true;
        }
        catch (Exception ex)
        {
            status = $"DRM telemetry unavailable: {ex.Message}";
            return false;
        }
    }

    public GpuSample Sample(int? processId) => new(UtilizationPercent: ReadDouble("gpu_busy_percent"), VramTotalBytes: ReadLong("mem_info_vram_total"), VramUsedBytes: ReadLong("mem_info_vram_used"));

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
