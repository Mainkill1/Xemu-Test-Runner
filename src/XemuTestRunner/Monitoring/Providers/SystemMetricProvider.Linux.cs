using System.Globalization;

namespace XemuTestRunner.Monitoring.Providers;

public sealed partial class SystemMetricProvider
{
    private static CpuCounters ReadLinuxCpu()
    {
        using var reader = File.OpenText("/proc/stat");
        var fields = reader.ReadLine()?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields is null || fields.Length < 5 || fields[0] != "cpu")
        {
            throw new InvalidDataException("The aggregate CPU row is missing from /proc/stat.");
        }

        // user, nice, system, idle, iowait, irq, softirq, steal.
        // guest and guest_nice are already included in user/nice; do not count twice.
        ulong total = 0;
        for (var index = 1; index < Math.Min(fields.Length, 9); index++)
        {
            total = checked(total + ulong.Parse(fields[index], CultureInfo.InvariantCulture));
        }

        var idle = ulong.Parse(fields[4], CultureInfo.InvariantCulture);
        if (fields.Length > 5)
        {
            idle = checked(idle + ulong.Parse(fields[5], CultureInfo.InvariantCulture));
        }

        return new CpuCounters(idle, total);
    }

    private static MemorySnapshot ReadLinuxMemory()
    {
        long? total = null;
        long? available = null;
        long? swapTotal = null;
        long? swapFree = null;

        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            // Do not allocate a dictionary for all kernel memory counters on every sample.
            var name = line.AsSpan(0, colon);
            if (name.SequenceEqual("MemTotal"))
            {
                total = ParseKilobytes(line.AsSpan(colon + 1));
            }
            else if (name.SequenceEqual("MemAvailable"))
            {
                available = ParseKilobytes(line.AsSpan(colon + 1));
            }
            else if (name.SequenceEqual("SwapTotal"))
            {
                swapTotal = ParseKilobytes(line.AsSpan(colon + 1));
            }
            else if (name.SequenceEqual("SwapFree"))
            {
                swapFree = ParseKilobytes(line.AsSpan(colon + 1));
            }
        }

        if (total is null || available is null)
        {
            throw new InvalidDataException("MemTotal or MemAvailable is missing from /proc/meminfo.");
        }

        long? swapUsed = swapTotal is not null && swapFree is not null
            ? Math.Max(0, swapTotal.Value - swapFree.Value)
            : null;
        return new MemorySnapshot(
            total, Math.Max(0, total.Value - available.Value), available,
            swapTotal, swapUsed, null);
    }

    private static long ParseKilobytes(ReadOnlySpan<char> value)
    {
        value = value.Trim();
        var space = value.IndexOf(' ');
        var number = space < 0 ? value : value[..space];
        return checked(long.Parse(number, NumberStyles.None, CultureInfo.InvariantCulture) * 1024);
    }

    private static IoCountersSnapshot ReadLinuxProcessIo(int processId)
    {
        long? read = null;
        long? write = null;
        foreach (var line in File.ReadLines($"/proc/{processId}/io"))
        {
            if (line.StartsWith("read_bytes:", StringComparison.Ordinal))
            {
                read = long.Parse(line.AsSpan("read_bytes:".Length).Trim(), CultureInfo.InvariantCulture);
            }
            else if (line.StartsWith("write_bytes:", StringComparison.Ordinal))
            {
                write = long.Parse(line.AsSpan("write_bytes:".Length).Trim(), CultureInfo.InvariantCulture);
            }
        }

        if (read is null || write is null)
        {
            throw new InvalidDataException("Process I/O byte counters are unavailable.");
        }

        return new IoCountersSnapshot(read.Value, write.Value);
    }
}
