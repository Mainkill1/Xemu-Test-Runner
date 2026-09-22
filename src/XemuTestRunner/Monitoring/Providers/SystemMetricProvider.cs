using System.ComponentModel;
using System.Diagnostics;
using System.Security;

namespace XemuTestRunner.Monitoring.Providers;

/// <summary>
/// Samples inexpensive host and target-process counters. One unavailable counter
/// must not stop the lifetime sampler or erase unrelated values.
/// </summary>
public sealed partial class SystemMetricProvider : IDisposable
{
    private CpuCounters? _previousHostCpu;
    private int? _processId;
    private TimeSpan _previousProcessCpu;
    private long _previousProcessTimestamp;
    private bool _hasProcessCpu;
    private IoCountersSnapshot? _previousIo;
    private long _previousIoTimestamp;

    public SystemSample Sample(Process? process, bool processIo)
    {
        var errors = new List<string>();
        double? hostCpu = null;
        MemorySnapshot memory = default;

        try
        {
            hostCpu = SampleHostCpu();
        }
        catch (Exception exception) when (IsCounterReadFailure(exception))
        {
            _previousHostCpu = null;
            errors.Add("host_cpu: " + exception.Message);
        }

        try
        {
            memory = ReadHostMemory();
        }
        catch (Exception exception) when (IsCounterReadFailure(exception))
        {
            errors.Add("memory: " + exception.Message);
        }

        double? processCpu = null;
        long? workingSet = null;
        long? privateBytes = null;
        double? readBytesPerSecond = null;
        double? writeBytesPerSecond = null;

        var processAvailable = RefreshTarget(process, errors);
        if (processAvailable && process is not null)
        {
            try
            {
                workingSet = process.WorkingSet64;
                privateBytes = process.PrivateMemorySize64;
            }
            catch (Exception exception) when (IsCounterReadFailure(exception))
            {
                errors.Add("process_memory: " + exception.Message);
            }

            try
            {
                processCpu = SampleProcessCpu(process);
            }
            catch (Exception exception) when (IsCounterReadFailure(exception))
            {
                _hasProcessCpu = false;
                errors.Add("process_cpu: " + exception.Message);
            }

            if (processIo)
            {
                try
                {
                    (readBytesPerSecond, writeBytesPerSecond) = SampleProcessIo(process);
                }
                catch (Exception exception) when (IsCounterReadFailure(exception))
                {
                    // /proc/<pid>/io can disappear or become inaccessible during
                    // exit. Re-prime on recovery instead of inventing a zero rate.
                    _previousIo = null;
                    errors.Add("process_io: " + exception.Message);
                }
            }
            else
            {
                _previousIo = null;
            }
        }

        return new SystemSample(
            hostCpu, processCpu,
            memory.Total, memory.Used, memory.Available,
            workingSet, privateBytes,
            memory.SwapTotal, memory.SwapUsed, memory.PageFileUsagePercent,
            readBytesPerSecond, writeBytesPerSecond, errors);
    }

    private bool RefreshTarget(Process? process, List<string> errors)
    {
        if (process is null)
        {
            ResetTarget(null);
            return false;
        }

        try
        {
            process.Refresh();
            if (process.HasExited)
            {
                ResetTarget(null);
                return false;
            }

            if (_processId != process.Id)
            {
                ResetTarget(process.Id);
            }

            return true;
        }
        catch (Exception exception) when (IsCounterReadFailure(exception))
        {
            ResetTarget(null);
            errors.Add("process: " + exception.Message);
            return false;
        }
    }

    private void ResetTarget(int? processId)
    {
        _processId = processId;
        _hasProcessCpu = false;
        _previousIo = null;
    }

    private double? SampleHostCpu()
    {
        CpuCounters? current = OperatingSystem.IsWindows()
            ? ReadWindowsCpu()
            : OperatingSystem.IsLinux() ? ReadLinuxCpu() : null;

        var previous = _previousHostCpu;
        _previousHostCpu = current;
        if (current is null || previous is null)
        {
            return null;
        }

        // Counter resets and Linux iowait corrections can move a value backward.
        // Such an interval is unknown, not an unsigned-underflow CPU spike.
        if (current.Value.Total <= previous.Value.Total ||
            current.Value.Idle < previous.Value.Idle)
        {
            return null;
        }

        var idleDelta = current.Value.Idle - previous.Value.Idle;
        var totalDelta = current.Value.Total - previous.Value.Total;
        return Math.Clamp((1.0 - (double)idleDelta / totalDelta) * 100.0, 0, 100);
    }

    private double? SampleProcessCpu(Process process)
    {
        var timestamp = Stopwatch.GetTimestamp();
        var cpuTime = process.TotalProcessorTime;
        var elapsedSeconds = (double)(timestamp - _previousProcessTimestamp) / Stopwatch.Frequency;
        var cpuSeconds = (cpuTime - _previousProcessCpu).TotalSeconds;
        var canCalculate = _hasProcessCpu && elapsedSeconds > 0 && cpuSeconds >= 0;

        _previousProcessTimestamp = timestamp;
        _previousProcessCpu = cpuTime;
        _hasProcessCpu = true;

        // Core percent deliberately exceeds 100 when the process uses multiple cores.
        return canCalculate ? cpuSeconds / elapsedSeconds * 100.0 : null;
    }

    private (double? Read, double? Write) SampleProcessIo(Process process)
    {
        IoCountersSnapshot? current = OperatingSystem.IsWindows()
            ? ReadWindowsProcessIo(process.Handle)
            : OperatingSystem.IsLinux() ? ReadLinuxProcessIo(process.Id) : null;

        var timestamp = Stopwatch.GetTimestamp();
        var previous = _previousIo;
        var elapsedSeconds = (double)(timestamp - _previousIoTimestamp) / Stopwatch.Frequency;
        _previousIo = current;
        _previousIoTimestamp = timestamp;

        if (current is null || previous is null || elapsedSeconds <= 0 ||
            current.Value.Read < previous.Value.Read || current.Value.Write < previous.Value.Write)
        {
            return (null, null);
        }

        return (
            (current.Value.Read - previous.Value.Read) / elapsedSeconds,
            (current.Value.Write - previous.Value.Write) / elapsedSeconds);
    }

    private static MemorySnapshot ReadHostMemory()
    {
        if (OperatingSystem.IsWindows())
        {
            return ReadWindowsMemory();
        }

        return OperatingSystem.IsLinux() ? ReadLinuxMemory() : default;
    }

    private static bool IsCounterReadFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or Win32Exception or
            InvalidOperationException or NotSupportedException or SecurityException or
            FormatException or OverflowException;

    // Providers use short-lived reads; the interface is retained for collector ownership.
    public void Dispose() { }

    private readonly record struct CpuCounters(ulong Idle, ulong Total);
    private readonly record struct IoCountersSnapshot(long Read, long Write);
    private readonly record struct MemorySnapshot(
        long? Total, long? Used, long? Available,
        long? SwapTotal, long? SwapUsed, double? PageFileUsagePercent);
}

public sealed record SystemSample(
    double? HostCpuPercent,
    double? ProcessCpuPercent,
    long? HostMemoryTotalBytes,
    long? HostMemoryUsedBytes,
    long? HostMemoryAvailableBytes,
    long? ProcessWorkingSetBytes,
    long? ProcessPrivateBytes,
    long? SwapTotalBytes,
    long? SwapUsedBytes,
    double? PageFileUsagePercent,
    double? ProcessReadBytesPerSecond,
    double? ProcessWriteBytesPerSecond,
    IReadOnlyList<string> Errors);
