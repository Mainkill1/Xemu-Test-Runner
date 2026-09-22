namespace XemuTestRunner.Workstation;

public sealed record WorkstationStateSnapshot
{
    public static WorkstationStateSnapshot Unsupported { get; } = new()
    {
        Supported = false,
        Platform = OperatingSystem.IsWindows() ? "windows" :
            OperatingSystem.IsLinux() ? "linux" : "unknown",
        DisplayState = "unknown",
        PowerState = "unknown",
        PowerSource = "unknown",
        TimestampUtc = DateTimeOffset.UtcNow
    };

    public bool Supported { get; init; }
    public string Platform { get; init; } = "unknown";
    public bool? SessionLocked { get; init; }
    public string? InputDesktop { get; init; }
    public string DisplayState { get; init; } = "unknown";
    public string PowerState { get; init; } = "unknown";
    public string PowerSource { get; init; } = "unknown";
    public int? BatteryPercent { get; init; }
    public bool? BatterySaver { get; init; }
    public long? UserIdleSeconds { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSessionChangeUtc { get; init; }
    public DateTimeOffset? LastDisplayChangeUtc { get; init; }
    public DateTimeOffset? LastSuspendUtc { get; init; }
    public DateTimeOffset? LastResumeUtc { get; init; }
    public string? LastEvent { get; init; }
    public long EventSequence { get; init; }

    public bool RenderingRisk =>
        SessionLocked == true ||
        DisplayState is "off" or "dimmed" ||
        PowerState is "suspending" or "suspended" ||
        BatterySaver == true;
}
