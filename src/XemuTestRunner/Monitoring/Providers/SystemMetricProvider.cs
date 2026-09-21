using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace XemuTestRunner.Monitoring.Providers;

public sealed class SystemMetricProvider : IDisposable
{
    private ulong _previousIdle;
    private ulong _previousTotal;
    private bool _hasHostCpu;
    private TimeSpan _previousProcessCpu;
    private long _previousProcessTimestamp;
    private bool _hasProcessCpu;
    private long _previousReadBytes;
    private long _previousWriteBytes;
    private long _previousIoTimestamp;
    private bool _hasProcessIo;
    private PerformanceCounter? _pageFileUsage;

    public SystemMetricProvider()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                _pageFileUsage = new PerformanceCounter("Paging File", "% Usage", "_Total", readOnly: true);
                _ = _pageFileUsage.NextValue();
            }
            catch
            {
                _pageFileUsage?.Dispose();
                _pageFileUsage = null;
            }
        }
    }

    public SystemSample Sample(Process? process, bool processIo)
    {
        var hostCpu = SampleHostCpu();
        var memory = SampleMemory();
        double? processCpu = null;
        long? workingSet = null;
        long? privateBytes = null;
        double? readBps = null;
        double? writeBps = null;

        if (process is not null)
        {
            try
            {
                process.Refresh();
                workingSet = process.WorkingSet64;
                privateBytes = process.PrivateMemorySize64;
                processCpu = SampleProcessCpu(process);
                if (processIo)
                    (readBps, writeBps) = SampleProcessIo(process);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        double? pageFileUsage = null;
        if (OperatingSystem.IsWindows() && _pageFileUsage is not null)
        {
            try { pageFileUsage = _pageFileUsage.NextValue(); } catch { }
        }

        return new SystemSample(hostCpu, processCpu, memory.Total, memory.Used, memory.Available, workingSet, privateBytes, memory.SwapTotal, memory.SwapUsed, pageFileUsage, readBps, writeBps);
    }

    private double? SampleHostCpu()
    {
        (ulong Idle, ulong Total)? values = OperatingSystem.IsWindows() ? ReadWindowsCpu() : OperatingSystem.IsLinux() ? ReadLinuxCpu() : null;
        if (values is null)
            return null;

        if (!_hasHostCpu)
        {
            _previousIdle = values.Value.Idle;
            _previousTotal = values.Value.Total;
            _hasHostCpu = true;
            return null;
        }

        var idleDelta = values.Value.Idle - _previousIdle;
        var totalDelta = values.Value.Total - _previousTotal;
        _previousIdle = values.Value.Idle;
        _previousTotal = values.Value.Total;
        return totalDelta == 0 ? null : Math.Clamp((1.0 - (double)idleDelta / totalDelta) * 100.0, 0, 100);
    }

    private double? SampleProcessCpu(Process process)
    {
        var now = Stopwatch.GetTimestamp();
        var cpu = process.TotalProcessorTime;
        if (!_hasProcessCpu)
        {
            _previousProcessTimestamp = now;
            _previousProcessCpu = cpu;
            _hasProcessCpu = true;
            return null;
        }

        var elapsedSeconds = (double)(now - _previousProcessTimestamp) / Stopwatch.Frequency;
        var cpuSeconds = (cpu - _previousProcessCpu).TotalSeconds;
        _previousProcessTimestamp = now;
        _previousProcessCpu = cpu;
        return elapsedSeconds <= 0 ? null : Math.Max(0, cpuSeconds / elapsedSeconds * 100.0);
    }

    private (double? ReadBps, double? WriteBps) SampleProcessIo(Process process)
    {
        (long Read, long Write)? io = OperatingSystem.IsWindows() ? ReadWindowsProcessIo(process.Handle) : OperatingSystem.IsLinux() ? ReadLinuxProcessIo(process.Id) : null;
        if (io is null)
            return (null, null);

        var now = Stopwatch.GetTimestamp();
        if (!_hasProcessIo)
        {
            _previousReadBytes = io.Value.Read;
            _previousWriteBytes = io.Value.Write;
            _previousIoTimestamp = now;
            _hasProcessIo = true;
            return (null, null);
        }

        var elapsed = (double)(now - _previousIoTimestamp) / Stopwatch.Frequency;
        if (elapsed <= 0)
            return (null, null);

        var read = Math.Max(0, io.Value.Read - _previousReadBytes) / elapsed;
        var write = Math.Max(0, io.Value.Write - _previousWriteBytes) / elapsed;
        _previousReadBytes = io.Value.Read;
        _previousWriteBytes = io.Value.Write;
        _previousIoTimestamp = now;
        return (read, write);
    }

    private static (long Total, long Used, long Available, long? SwapTotal, long? SwapUsed) SampleMemory()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx();
            if (GlobalMemoryStatusEx(status))
            {
                var total = checked((long)status.TotalPhys);
                var available = checked((long)status.AvailPhys);
                return (total, total - available, available, null, null);
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            try
            {
                var values = File.ReadLines("/proc/meminfo")
                    .Select(line => line.Split(':', 2))
                    .Where(parts => parts.Length == 2)
                    .ToDictionary(parts => parts[0], parts => ParseMemInfoBytes(parts[1]), StringComparer.Ordinal);
                var total = values.GetValueOrDefault("MemTotal");
                var available = values.GetValueOrDefault("MemAvailable");
                var swapTotal = values.GetValueOrDefault("SwapTotal");
                var swapFree = values.GetValueOrDefault("SwapFree");
                return (total, total - available, available, swapTotal, swapTotal - swapFree);
            }
            catch { }
        }

        var info = GC.GetGCMemoryInfo();
        var fallbackTotal = info.TotalAvailableMemoryBytes;
        return (fallbackTotal, 0, fallbackTotal, null, null);
    }

    private static long ParseMemInfoBytes(string value)
    {
        var first = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return long.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb) ? kb * 1024 : 0;
    }

    private static (ulong Idle, ulong Total)? ReadLinuxCpu()
    {
        try
        {
            var line = File.ReadLines("/proc/stat").First();
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || parts[0] != "cpu")
                return null;
            ulong total = 0;
            for (var i = 1; i < parts.Length; i++)
                if (ulong.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    total += value;
            var idle = ulong.Parse(parts[4], CultureInfo.InvariantCulture);
            if (parts.Length > 5 && ulong.TryParse(parts[5], out var ioWait))
                idle += ioWait;
            return (idle, total);
        }
        catch { return null; }
    }

    private static (ulong Idle, ulong Total)? ReadWindowsCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return null;
        var idleValue = ToUInt64(idle);
        return (idleValue, ToUInt64(kernel) + ToUInt64(user));
    }

    private static (long Read, long Write)? ReadLinuxProcessIo(int pid)
    {
        try
        {
            long read = 0;
            long write = 0;
            foreach (var line in File.ReadLines($"/proc/{pid}/io"))
            {
                if (line.StartsWith("read_bytes:", StringComparison.Ordinal))
                    long.TryParse(line.AsSpan("read_bytes:".Length).Trim(), out read);
                else if (line.StartsWith("write_bytes:", StringComparison.Ordinal))
                    long.TryParse(line.AsSpan("write_bytes:".Length).Trim(), out write);
            }
            return (read, write);
        }
        catch { return null; }
    }

    private static (long Read, long Write)? ReadWindowsProcessIo(IntPtr handle) =>
        GetProcessIoCounters(handle, out var counters) ? (checked((long)counters.ReadTransferCount), checked((long)counters.WriteTransferCount)) : null;

    private static ulong ToUInt64(FileTime fileTime) => ((ulong)fileTime.High << 32) | fileTime.Low;
    public void Dispose() => _pageFileUsage?.Dispose();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr processHandle, out IoCounters counters);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low; public uint High; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }
}

public sealed record SystemSample(double? HostCpuPercent, double? ProcessCpuPercent, long? HostMemoryTotalBytes, long? HostMemoryUsedBytes, long? HostMemoryAvailableBytes, long? ProcessWorkingSetBytes, long? ProcessPrivateBytes, long? SwapTotalBytes, long? SwapUsedBytes, double? PageFileUsagePercent, double? ProcessReadBytesPerSecond, double? ProcessWriteBytesPerSecond);
