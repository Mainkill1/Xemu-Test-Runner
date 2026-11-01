using System.Runtime.InteropServices;

namespace XemuTestRunner.Runtime;

/// <summary>Native coordinates used by QEMU_CLOCK_REALTIME, without assuming a Stopwatch epoch.</summary>
internal static class PerformanceClock
{
    internal sealed record Anchor(string Domain, long TimestampUs);

    public static Anchor? Capture()
    {
        try
        {
            if (OperatingSystem.IsLinux() && Environment.Is64BitProcess)
            {
                // Linux CLOCK_MONOTONIC; the supported 64-bit ABI has two signed 64-bit fields.
                if (ClockGetTime(1, out var time) != 0 || time.Seconds < 0 ||
                    time.Nanoseconds is < 0 or >= 1_000_000_000) return null;
                return AnchorAt("linux-clock-monotonic",
                    (Int128)time.Seconds * 1_000_000 + time.Nanoseconds / 1000);
            }
            if (OperatingSystem.IsWindows())
            {
                if (!QueryPerformanceFrequency(out var frequency) || frequency <= 0 ||
                    !QueryPerformanceCounter(out var counter) || counter < 0) return null;
                return AnchorAt("windows-qpc", (Int128)counter * 1_000_000 / frequency);
            }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            // Legacy descriptive runs remain usable; opt-in analysis rejects missing anchors.
        }
        return null;
    }

    private static Anchor? AnchorAt(string domain, Int128 timestampUs) =>
        timestampUs >= 0 && timestampUs <= 9_007_199_254_740_991
            ? new(domain, (long)timestampUs) : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    [DllImport("libc", EntryPoint = "clock_gettime", SetLastError = true)]
    private static extern int ClockGetTime(int clockId, out Timespec time);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryPerformanceCounter(out long counter);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryPerformanceFrequency(out long frequency);
}
