namespace XemuTestRunner.Config;

public sealed class RunnerConfig
{
    public string Workspace { get; set; } = "workspace";
    public QueueOptions Queue { get; set; } = new();
    public MonitoringOptions Monitoring { get; set; } = new();
    public HttpOptions Http { get; set; } = new();
}

public sealed class QueueOptions
{
    public string Pending { get; set; } = "Queue/Pending";
    public string Testing { get; set; } = "Queue/Testing";
    public string Tested { get; set; } = "Queue/Tested";
    public string Results { get; set; } = "Results";
    public int ScanIntervalMs { get; set; } = 500;
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
    public int CounterRefreshMs { get; set; } = 5000;
}

public sealed class HttpOptions
{
    public bool Enabled { get; set; } = true;
    public string BindAddress { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 9368;
    public string FileRoot { get; set; } = "Files";
    public int TransferBufferBytes { get; set; } = 1024 * 1024;
    public int MaxHeaderBytes { get; set; } = 64 * 1024;
}

public sealed record RunnerPaths(
    string ConfigFile,
    string Workspace,
    string Pending,
    string Testing,
    string Tested,
    string Results,
    string FileRoot);
