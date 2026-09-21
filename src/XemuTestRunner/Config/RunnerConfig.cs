using XemuTestRunner.Reliability;
using XemuTestRunner.Diagnostics;

namespace XemuTestRunner.Config;

public sealed class RunnerConfig
{
    public string Workspace { get; set; } = "workspace";
    public QueueOptions Queue { get; set; } = new();
    public MonitoringOptions Monitoring { get; set; } = new();
    public HttpOptions Http { get; set; } = new();
    public XemuControlOptions XemuControl { get; set; } = new();
    public UiOptions Ui { get; set; } = new();
    public ReliabilityOptions Reliability { get; set; } = new();
    public DiagnosticsOptions Diagnostics { get; set; } = new();
}
public sealed class QueueOptions
{
    public string Pending { get; set; } = "Queue/Pending";
    public string Testing { get; set; } = "Queue/Testing";
    public string Tested { get; set; } = "Queue/Tested";
    public string Results { get; set; } = "Results";
    public int ScanIntervalMs { get; set; } = 500;
    public int PackageStabilityMs { get; set; } = 750;
    public string InterruptedAction { get; set; } = "retry";
}
public sealed class MonitoringOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalMs { get; set; } = 100;
    public int FlushIntervalMs { get; set; } = 1000;
    public int BufferCapacity { get; set; } = 8192;
    public bool ProcessIo { get; set; } = true;
    public GpuOptions Gpu { get; set; } = new();
}
public sealed class GpuOptions
{
    public bool Enabled { get; set; } = true;
    public string Provider { get; set; } = "auto";
    public int DeviceIndex { get; set; } = 0;
    public int SampleIntervalMs { get; set; } = 250;
    public int SensorIntervalMs { get; set; } = 1000;
    public int CounterRefreshMs { get; set; } = 5000;
}
public sealed class HttpOptions
{
    public bool Enabled { get; set; } = true;
    public string BindAddress { get; set; } = "0.0.0.0";
    public string? AdvertiseAddress { get; set; }
    public int Port { get; set; } = 9368;
    public string FileRoot { get; set; } = "Files";
    public int TransferBufferBytes { get; set; } = 1024 * 1024;
    public int MaxHeaderBytes { get; set; } = 64 * 1024;
}
public sealed class UiOptions
{
    public int CliRefreshMs { get; set; } = 250;
    public int WebRefreshMs { get; set; } = 500;
    public bool LivePreviewEnabled { get; set; } = true;
    public int LivePreviewIntervalMs { get; set; } = 750;
}
public sealed class XemuControlOptions
{
    public bool Enabled { get; set; } = true;
    public string QmpHost { get; set; } = "127.0.0.1";
    public int QmpPort { get; set; }
    public int ConnectTimeoutMs { get; set; } = 10000;
    public int ScreenshotTimeoutMs { get; set; } = 5000;
    public string ScreenshotProvider { get; set; } = "auto";
    public string ScreenshotExecutable { get; set; } = "";
    public List<string> ScreenshotArguments { get; set; } = [];
    public string InputProvider { get; set; } = "auto";
    public int DefaultButtonHoldMs { get; set; } = 100;
    public Dictionary<string, string> ButtonKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = "a", ["B"] = "b", ["X"] = "x", ["Y"] = "y", ["Back"] = "backspace", ["Start"] = "enter",
        ["White"] = "1", ["Black"] = "2", ["LStick"] = "3", ["RStick"] = "4", ["Guide"] = "5",
        ["DPadUp"] = "up", ["DPadDown"] = "down", ["DPadLeft"] = "left", ["DPadRight"] = "right",
        ["LStickUp"] = "e", ["LStickDown"] = "d", ["LStickLeft"] = "s", ["LStickRight"] = "f", ["LTrigger"] = "w",
        ["RStickUp"] = "i", ["RStickDown"] = "k", ["RStickLeft"] = "j", ["RStickRight"] = "l", ["RTrigger"] = "o"
    };
}
public sealed record RunnerPaths(string ConfigFile, string Workspace, string Pending, string Testing, string Tested, string Results, string FileRoot);
