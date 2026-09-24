namespace XemuTestRunner.Reliability;

public sealed class ReliabilityOptions
{
    public PreflightOptions Preflight { get; set; } = new();
    public WatchdogOptions Watchdog { get; set; } = new();
    public int MaxInterruptedRetries { get; set; } = 2;
    public int ProcessExitTimeoutMs { get; set; } = 10000;
    public int EvidenceListLimit { get; set; } = 100;
    public int MaxPreviewBytes { get; set; } = 16 * 1024 * 1024;

    // Automated queues terminate failed targets by default. Preserving a live
    // reproduction is an explicit interactive-debug choice, not crash recovery.
    public bool PreserveTargetOnRunnerError { get; set; }

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
