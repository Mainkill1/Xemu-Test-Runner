using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using XemuTestRunner.Monitoring;

namespace XemuTestRunner.Runtime;

public sealed record HostInventory(
    DateTimeOffset CapturedUtc,
    string Machine,
    string OperatingSystem,
    string OsArchitecture,
    string ProcessArchitecture,
    string DotNet,
    string? CpuModel,
    int LogicalProcessors,
    long? PhysicalMemoryBytes,
    string? PrimaryDisplay,
    string? PowerScheme,
    IReadOnlyList<GraphicsAdapterInventory> GraphicsAdapters);

public sealed record GraphicsAdapterInventory(
    string Name,
    string? DeviceId,
    string? Driver);

public static class HostInventoryCollector
{
    public static async Task<HostInventory> CaptureAsync(
        MetricSample? currentMetric,
        CancellationToken cancellationToken)
    {
        var adapters = OperatingSystem.IsWindows()
            ? ReadWindowsDisplayAdapters()
            : OperatingSystem.IsLinux()
                ? ReadLinuxDrmAdapters()
                : [];

        var cpu = OperatingSystem.IsWindows()
            ? ReadWindowsCpuModel()
            : OperatingSystem.IsLinux()
                ? ReadLinuxCpuModel()
                : null;

        var display = OperatingSystem.IsWindows()
            ? ReadWindowsPrimaryDisplay()
            : null;

        var power = OperatingSystem.IsWindows()
            ? await ReadWindowsPowerSchemeAsync(
                cancellationToken).ConfigureAwait(false)
            : null;

        return new HostInventory(
            DateTimeOffset.UtcNow,
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.Version.ToString(),
            cpu,
            Environment.ProcessorCount,
            currentMetric?.HostMemoryTotalBytes,
            display,
            power,
            adapters);
    }

    private static string? ReadWindowsCpuModel()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString")
                ?.ToString()
                ?.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadLinuxCpuModel()
    {
        try
        {
            foreach (var line in File.ReadLines(
                         "/proc/cpuinfo"))
            {
                if (line.StartsWith(
                        "model name",
                        StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith(
                        "Hardware",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var colon = line.IndexOf(':');
                    if (colon >= 0)
                        return line[(colon + 1)..].Trim();
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static IReadOnlyList<GraphicsAdapterInventory>
        ReadWindowsDisplayAdapters()
    {
        if (!OperatingSystem.IsWindows())
            return [];

        var result =
            new List<GraphicsAdapterInventory>();

        for (uint index = 0; index < 32; index++)
        {
            var device = new DisplayDevice
            {
                Size = Marshal.SizeOf<DisplayDevice>()
            };

            if (!EnumDisplayDevicesW(
                    null,
                    index,
                    ref device,
                    0))
                break;

            if (string.IsNullOrWhiteSpace(
                    device.DeviceString))
                continue;

            result.Add(
                new GraphicsAdapterInventory(
                    device.DeviceString.Trim(),
                    string.IsNullOrWhiteSpace(
                        device.DeviceId)
                        ? null
                        : device.DeviceId.Trim(),
                    null));
        }

        return result
            .DistinctBy(
                adapter =>
                    adapter.DeviceId ??
                    adapter.Name,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<GraphicsAdapterInventory>
        ReadLinuxDrmAdapters()
    {
        var result =
            new List<GraphicsAdapterInventory>();

        try
        {
            foreach (var card in Directory.EnumerateDirectories(
                         "/sys/class/drm",
                         "card*"))
            {
                var name = Path.GetFileName(card);
                if (name.Contains('-', StringComparison.Ordinal))
                    continue;

                var uevent =
                    Path.Combine(
                        card,
                        "device",
                        "uevent");
                if (!File.Exists(uevent))
                    continue;

                string? pci = null;
                string? driver = null;

                foreach (var line in File.ReadLines(uevent))
                {
                    if (line.StartsWith(
                            "PCI_ID=",
                            StringComparison.Ordinal))
                        pci = line["PCI_ID=".Length..];
                    else if (line.StartsWith(
                                 "DRIVER=",
                                 StringComparison.Ordinal))
                        driver = line["DRIVER=".Length..];
                }

                result.Add(
                    new GraphicsAdapterInventory(
                        name,
                        pci,
                        driver));
            }
        }
        catch
        {
        }

        return result;
    }

    private static string? ReadWindowsPrimaryDisplay()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            var width = GetSystemMetrics(0);
            var height = GetSystemMetrics(1);
            return width > 0 && height > 0
                ? $"{width}x{height}"
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?>
        ReadWindowsPowerSchemeAsync(
            CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo(
                "powercfg.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(
                "/getactivescheme");

            using var process =
                Process.Start(startInfo);
            if (process is null)
                return null;

            using var deadline =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            deadline.CancelAfter(
                TimeSpan.FromSeconds(2));

            await process.WaitForExitAsync(
                    deadline.Token)
                .ConfigureAwait(false);

            if (process.ExitCode != 0)
                return null;

            var output =
                await process.StandardOutput
                    .ReadToEndAsync(
                        cancellationToken)
                    .ConfigureAwait(false);

            return output.Trim();
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(
        LayoutKind.Sequential,
        CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;

        [MarshalAs(
            UnmanagedType.ByValTStr,
            SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(
            UnmanagedType.ByValTStr,
            SizeConst = 128)]
        public string DeviceString;

        public uint StateFlags;

        [MarshalAs(
            UnmanagedType.ByValTStr,
            SizeConst = 128)]
        public string DeviceId;

        [MarshalAs(
            UnmanagedType.ByValTStr,
            SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevicesW(
        string? device,
        uint deviceNumber,
        ref DisplayDevice displayDevice,
        uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(
        int index);
}
