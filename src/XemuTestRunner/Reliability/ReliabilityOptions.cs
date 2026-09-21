namespace XemuTestRunner.Reliability;

public sealed class ReliabilityOptions
{
    public PreflightOptions Preflight { get; set; } = new();
    public WatchdogOptions Watchdog { get; set; } = new();
    public int MaxInterruptedRetries { get; set; } = 2;
    public int ProcessExitTimeoutMs { get; set; } = 10000;
    public int EvidenceListLimit { get; set; } = 100;
    public int MaxPreviewBytes { get; set; } = 16 * 1024 * 1024;

    // Runner/control failures are evidence about the harness, not proof that xemu
    // itself is bad. Preserve the target so an operator can inspect it instead
    // of immediately destroying the reproduction.
    public bool PreserveTargetOnRunnerError { get; set; } = true;
}

public sealed class PreflightOptions
{
    public long MinimumFreeSpaceBytes { get; set; } = 1024L * 1024 * 1024;
}

public sealed class WatchdogOptions
{
    public bool Enabled { get; set; } = true;
    public int StartupGraceMs { get; set; } = 10000;
    public int IntervalMs { get; set; } = 2000;
    public int RequestTimeoutMs { get; set; } = 5000;
    public int FailureThreshold { get; set; } = 3;
}
