namespace XemuTestRunner.Authoring;

[Flags]
public enum XboxControllerButtons : ushort
{
    None = 0,
    DPadUp = 0x0001,
    DPadDown = 0x0002,
    DPadLeft = 0x0004,
    DPadRight = 0x0008,
    Start = 0x0010,
    Back = 0x0020,
    LeftStick = 0x0040,
    RightStick = 0x0080,
    LeftShoulder = 0x0100,
    RightShoulder = 0x0200,
    Guide = 0x0400,
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000
}

public readonly record struct XboxControllerState(
    XboxControllerButtons Buttons,
    byte LeftTrigger,
    byte RightTrigger,
    short LeftX,
    short LeftY,
    short RightX,
    short RightY)
{
    public static XboxControllerState Neutral => default;
}

public sealed record BrowserControllerPacket(
    ulong Sequence,
    long BrowserTimestampUs,
    int ControllerIndex,
    XboxControllerState State)
{
    public void Validate()
    {
        if (ControllerIndex is < 0 or > 3)
            throw new InvalidDataException("ControllerIndex must be between 0 and 3.");
        if (BrowserTimestampUs < 0)
            throw new InvalidDataException("BrowserTimestampUs cannot be negative.");
    }
}

public readonly record struct InputHoldRequirement(
    int MinimumElapsedMs,
    int MinimumPresentedFrames,
    int TimeoutMs)
{
    public void Validate()
    {
        if (MinimumElapsedMs < 0)
            throw new InvalidDataException("MinimumElapsedMs cannot be negative.");
        if (MinimumPresentedFrames < 0)
            throw new InvalidDataException("MinimumPresentedFrames cannot be negative.");
        if (TimeoutMs <= 0)
            throw new InvalidDataException("TimeoutMs must be greater than zero.");
    }
}

public sealed record AppliedControllerSample(
    ulong Sequence,
    long AppliedAtUs,
    long PresentedFrame,
    int ControllerIndex,
    XboxControllerState State,
    InputHoldRequirement HoldRequirement);

public static class InputReplayGate
{
    public static bool IsSatisfied(
        InputHoldRequirement requirement,
        TimeSpan elapsed,
        long presentedFrames)
    {
        requirement.Validate();
        if (elapsed < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(elapsed), elapsed, "Elapsed time cannot be negative.");
        if (presentedFrames < 0)
            throw new ArgumentOutOfRangeException(
                nameof(presentedFrames),
                presentedFrames,
                "Presented frame progress cannot be negative.");

        return elapsed.TotalMilliseconds >= requirement.MinimumElapsedMs &&
               presentedFrames >= requirement.MinimumPresentedFrames;
    }
}
