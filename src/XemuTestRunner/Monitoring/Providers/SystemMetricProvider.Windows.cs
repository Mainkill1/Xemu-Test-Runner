using System.ComponentModel;
using System.Runtime.InteropServices;

namespace XemuTestRunner.Monitoring.Providers;

public sealed partial class SystemMetricProvider
{
    private static CpuCounters ReadWindowsCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        // GetSystemTimes includes idle time in the kernel count.
        return new CpuCounters(ToUInt64(idle), ToUInt64(kernel) + ToUInt64(user));
    }

    private static MemorySnapshot ReadWindowsMemory()
    {
        var status = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(status))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var total = checked((long)status.TotalPhys);
        var available = checked((long)status.AvailPhys);
        double? commitUsage = status.TotalPageFile == 0
            ? null
            : Math.Clamp((1.0 - (double)status.AvailPageFile / status.TotalPageFile) * 100.0, 0, 100);

        // PageFileUsagePercent is the legacy API field name. These native fields
        // describe commit pressure, not the bytes physically stored in pagefile.sys.
        return new MemorySnapshot(
            total, Math.Max(0, total - available), available, null, null, commitUsage);
    }

    private static IoCountersSnapshot ReadWindowsProcessIo(IntPtr processHandle)
    {
        if (!GetProcessIoCounters(processHandle, out var counters))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return new IoCountersSnapshot(
            checked((long)counters.ReadTransferCount),
            checked((long)counters.WriteTransferCount));
    }

    private static ulong ToUInt64(FileTime time) => ((ulong)time.High << 32) | time.Low;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr processHandle, out NativeIoCounters counters);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
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
    private struct NativeIoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }
}
