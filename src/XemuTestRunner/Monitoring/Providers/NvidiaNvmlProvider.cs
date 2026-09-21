using System.Runtime.InteropServices;
using XemuTestRunner.Config;

namespace XemuTestRunner.Monitoring.Providers;

public sealed class NvidiaNvmlProvider : IGpuMetricProvider
{
    private const int Success = 0;
    private readonly IntPtr _library;
    private readonly IntPtr _device;
    private readonly NvmlShutdown _shutdown;
    private readonly NvmlGetUtilization _getUtilization;
    private readonly NvmlGetMemoryInfo _getMemoryInfo;
    private readonly NvmlGetTemperature? _getTemperature;
    private readonly NvmlGetPowerUsage? _getPowerUsage;
    private readonly int _sampleIntervalMs;
    private readonly int _sensorIntervalMs;
    private long _nextFastSampleTick;
    private long _nextSensorSampleTick;
    private double? _cachedUtilization;
    private long? _cachedTotal;
    private long? _cachedUsed;
    private double? _cachedTemperature;
    private double? _cachedPower;
    private bool _disposed;
    public string Name => "NVIDIA NVML";

    private NvidiaNvmlProvider(
        IntPtr library,
        IntPtr device,
        NvmlShutdown shutdown,
        NvmlGetUtilization getUtilization,
        NvmlGetMemoryInfo getMemoryInfo,
        NvmlGetTemperature? getTemperature,
        NvmlGetPowerUsage? getPowerUsage,
        int sampleIntervalMs,
        int sensorIntervalMs)
    {
        _library = library;
        _device = device;
        _shutdown = shutdown;
        _getUtilization = getUtilization;
        _getMemoryInfo = getMemoryInfo;
        _getTemperature = getTemperature;
        _getPowerUsage = getPowerUsage;
        _sampleIntervalMs = sampleIntervalMs;
        _sensorIntervalMs = sensorIntervalMs;
    }

    public static bool TryCreate(GpuOptions options, out NvidiaNvmlProvider? provider, out string status)
    {
        provider = null;
        status = "NVML library not found.";
        var names = OperatingSystem.IsWindows() ? new[] { "nvml.dll" } : new[] { "libnvidia-ml.so.1", "libnvidia-ml.so" };
        IntPtr library = IntPtr.Zero;
        foreach (var name in names)
            if (NativeLibrary.TryLoad(name, out library)) break;
        if (library == IntPtr.Zero) return false;

        try
        {
            var init = GetRequired<NvmlInit>(library, "nvmlInit_v2");
            var shutdown = GetRequired<NvmlShutdown>(library, "nvmlShutdown");
            var getHandle = GetRequired<NvmlGetHandleByIndex>(library, "nvmlDeviceGetHandleByIndex_v2");
            var getUtil = GetRequired<NvmlGetUtilization>(library, "nvmlDeviceGetUtilizationRates");
            var getMemory = GetRequired<NvmlGetMemoryInfo>(library, "nvmlDeviceGetMemoryInfo");
            var getTemperature = GetOptional<NvmlGetTemperature>(library, "nvmlDeviceGetTemperature");
            var getPower = GetOptional<NvmlGetPowerUsage>(library, "nvmlDeviceGetPowerUsage");

            if (init() != Success)
            {
                status = "NVML initialization failed.";
                NativeLibrary.Free(library);
                return false;
            }
            if (getHandle((uint)options.DeviceIndex, out var device) != Success)
            {
                shutdown();
                NativeLibrary.Free(library);
                status = $"NVML GPU index {options.DeviceIndex} is unavailable.";
                return false;
            }

            provider = new NvidiaNvmlProvider(
                library,
                device,
                shutdown,
                getUtil,
                getMemory,
                getTemperature,
                getPower,
                options.SampleIntervalMs,
                options.SensorIntervalMs);
            status = $"NVML GPU index {options.DeviceIndex} available.";
            return true;
        }
        catch (Exception ex)
        {
            if (library != IntPtr.Zero) NativeLibrary.Free(library);
            status = $"NVML unavailable: {ex.Message}";
            return false;
        }
    }

    public GpuSample Sample(int? processId)
    {
        var now = Environment.TickCount64;

        if (_nextFastSampleTick == 0 || now >= _nextFastSampleTick)
        {
            _nextFastSampleTick = now + _sampleIntervalMs;

            if (_getUtilization(_device, out var util) == Success)
                _cachedUtilization = util.Gpu;

            if (_getMemoryInfo(_device, out var memory) == Success)
            {
                _cachedTotal = checked((long)memory.Total);
                _cachedUsed = checked((long)memory.Used);
            }
        }

        if (_nextSensorSampleTick == 0 || now >= _nextSensorSampleTick)
        {
            _nextSensorSampleTick = now + _sensorIntervalMs;

            if (_getTemperature is not null &&
                _getTemperature(_device, 0, out var temp) == Success)
                _cachedTemperature = temp;

            if (_getPowerUsage is not null &&
                _getPowerUsage(_device, out var milliwatts) == Success)
                _cachedPower = milliwatts / 1000.0;
        }

        return new GpuSample(
            UtilizationPercent: _cachedUtilization,
            VramTotalBytes: _cachedTotal,
            VramUsedBytes: _cachedUsed,
            TemperatureC: _cachedTemperature,
            PowerWatts: _cachedPower);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _shutdown(); } catch { }
        NativeLibrary.Free(_library);
    }

    private static T GetRequired<T>(IntPtr library, string name) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(library, name, out var export)) throw new EntryPointNotFoundException(name);
        return Marshal.GetDelegateForFunctionPointer<T>(export);
    }

    private static T? GetOptional<T>(IntPtr library, string name) where T : Delegate => NativeLibrary.TryGetExport(library, name, out var export) ? Marshal.GetDelegateForFunctionPointer<T>(export) : null;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlInit();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlShutdown();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlGetHandleByIndex(uint index, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlGetUtilization(IntPtr device, out NvmlUtilization utilization);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlGetMemoryInfo(IntPtr device, out NvmlMemory memory);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlGetTemperature(IntPtr device, uint sensorType, out uint temperature);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlGetPowerUsage(IntPtr device, out uint milliwatts);

    [StructLayout(LayoutKind.Sequential)] private struct NvmlUtilization { public uint Gpu; public uint Memory; }
    [StructLayout(LayoutKind.Sequential)] private struct NvmlMemory { public ulong Total; public ulong Free; public ulong Used; }
}
