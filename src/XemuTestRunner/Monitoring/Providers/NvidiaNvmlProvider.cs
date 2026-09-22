using System.Runtime.InteropServices;
using XemuTestRunner.Config;

namespace XemuTestRunner.Monitoring.Providers;

/// <summary>
/// Reads device-wide NVIDIA counters on two cached schedules. A failed refresh
/// invalidates that value; it must not keep presenting an old reading as current.
/// </summary>
public sealed class NvidiaNvmlProvider : IGpuMetricProvider
{
    private const int Success = 0;
    private const uint GpuTemperatureSensor = 0;
    private const double MilliwattsPerWatt = 1000.0;

    private readonly IntPtr _library;
    private readonly IntPtr _device;
    private readonly NvmlShutdown _shutdown;
    private readonly NvmlGetUtilization _getUtilization;
    private readonly NvmlGetMemoryInfo _getMemoryInfo;
    private readonly NvmlGetTemperature? _getTemperature;
    private readonly NvmlGetPowerUsage? _getPowerUsage;
    private readonly int _sampleIntervalMs;
    private readonly int _sensorIntervalMs;

    private long _nextUtilizationSample;
    private long _nextSensorSample;
    private double? _cachedUtilization;
    private long? _cachedVramTotal;
    private long? _cachedVramUsed;
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

    public static bool TryCreate(
        GpuOptions options,
        out NvidiaNvmlProvider? provider,
        out string status)
    {
        ArgumentNullException.ThrowIfNull(options);
        provider = null;

        if (options.DeviceIndex < 0 || options.SampleIntervalMs <= 0 || options.SensorIntervalMs <= 0)
        {
            status = "NVML requires a non-negative GPU index and positive polling intervals.";
            return false;
        }

        var library = LoadLibrary();
        if (library == IntPtr.Zero)
        {
            status = "NVML library not found.";
            return false;
        }

        NvmlShutdown? shutdown = null;
        var initialized = false;
        var ownershipTransferred = false;

        try
        {
            var initialize = GetRequired<NvmlInit>(library, "nvmlInit_v2");
            shutdown = GetRequired<NvmlShutdown>(library, "nvmlShutdown");
            var getHandle = GetRequired<NvmlGetHandleByIndex>(library, "nvmlDeviceGetHandleByIndex_v2");
            var getUtilization = GetRequired<NvmlGetUtilization>(library, "nvmlDeviceGetUtilizationRates");
            var getMemory = GetRequired<NvmlGetMemoryInfo>(library, "nvmlDeviceGetMemoryInfo");
            var getTemperature = GetOptional<NvmlGetTemperature>(library, "nvmlDeviceGetTemperature");
            var getPower = GetOptional<NvmlGetPowerUsage>(library, "nvmlDeviceGetPowerUsage");

            var initializationResult = initialize();
            if (initializationResult != Success)
            {
                status = $"NVML initialization failed (code {initializationResult}).";
                return false;
            }

            initialized = true;
            var deviceResult = getHandle((uint)options.DeviceIndex, out var device);
            if (deviceResult != Success)
            {
                status = $"NVML GPU index {options.DeviceIndex} is unavailable (code {deviceResult}).";
                return false;
            }

            provider = new NvidiaNvmlProvider(
                library,
                device,
                shutdown,
                getUtilization,
                getMemory,
                getTemperature,
                getPower,
                options.SampleIntervalMs,
                options.SensorIntervalMs);

            status = $"NVML GPU index {options.DeviceIndex} available.";
            ownershipTransferred = true;
            return true;
        }
        catch (Exception exception)
        {
            provider = null;
            status = $"NVML unavailable: {exception.Message}";
            return false;
        }
        finally
        {
            // Only the successful provider owns these resources. Every other
            // path balances successful initialization before unloading the DLL.
            if (!ownershipTransferred)
            {
                try
                {
                    if (initialized)
                    {
                        _ = shutdown!();
                    }
                }
                finally
                {
                    NativeLibrary.Free(library);
                }
            }
        }
    }

    public GpuSample Sample(int? processId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var now = Environment.TickCount64;

        if (now >= _nextUtilizationSample)
        {
            _nextUtilizationSample = now + _sampleIntervalMs;
            RefreshUtilization();
        }

        if (now >= _nextSensorSample)
        {
            _nextSensorSample = now + _sensorIntervalMs;
            RefreshMemory();
            RefreshTemperature();
            RefreshPower();
        }

        // These APIs describe the device, not processId. Process counters can be
        // supplied by another provider without changing this provider's scope.
        return new GpuSample(
            UtilizationPercent: _cachedUtilization,
            VramTotalBytes: _cachedVramTotal,
            VramUsedBytes: _cachedVramUsed,
            TemperatureC: _cachedTemperature,
            PowerWatts: _cachedPower);
    }

    private void RefreshUtilization()
    {
        _cachedUtilization = _getUtilization(_device, out var utilization) == Success
            ? utilization.Gpu
            : null;
    }

    private void RefreshMemory()
    {
        if (_getMemoryInfo(_device, out var memory) == Success &&
            memory.Total <= long.MaxValue && memory.Used <= memory.Total)
        {
            _cachedVramTotal = (long)memory.Total;
            _cachedVramUsed = (long)memory.Used;
        }
        else
        {
            _cachedVramTotal = null;
            _cachedVramUsed = null;
        }
    }

    private void RefreshTemperature()
    {
        _cachedTemperature = _getTemperature is not null &&
            _getTemperature(_device, GpuTemperatureSensor, out var temperature) == Success
                ? temperature
                : null;
    }

    private void RefreshPower()
    {
        _cachedPower = _getPowerUsage is not null &&
            _getPowerUsage(_device, out var milliwatts) == Success
                ? milliwatts / MilliwattsPerWatt
                : null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _ = _shutdown();
        }
        finally
        {
            NativeLibrary.Free(_library);
        }
    }

    private static IntPtr LoadLibrary()
    {
        string[] names = OperatingSystem.IsWindows()
            ? ["nvml.dll"]
            : ["libnvidia-ml.so.1", "libnvidia-ml.so"];

        foreach (var name in names)
        {
            if (NativeLibrary.TryLoad(name, out var library))
            {
                return library;
            }
        }

        return IntPtr.Zero;
    }

    private static T GetRequired<T>(IntPtr library, string name) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(library, name, out var export))
        {
            throw new EntryPointNotFoundException(name);
        }

        return Marshal.GetDelegateForFunctionPointer<T>(export);
    }

    private static T? GetOptional<T>(IntPtr library, string name) where T : Delegate
    {
        return NativeLibrary.TryGetExport(library, name, out var export)
            ? Marshal.GetDelegateForFunctionPointer<T>(export)
            : null;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlInit();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlShutdown();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlGetHandleByIndex(uint index, out IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlGetUtilization(IntPtr device, out NvmlUtilization utilization);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlGetMemoryInfo(IntPtr device, out NvmlMemory memory);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlGetTemperature(IntPtr device, uint sensorType, out uint temperature);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlGetPowerUsage(IntPtr device, out uint milliwatts);

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }
}
