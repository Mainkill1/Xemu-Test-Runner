using System.Diagnostics;

namespace XemuTestRunner.Control.Gamepad;

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

public sealed record NativeGamepadReceipt(
    ulong Sequence,
    long AppliedAtUs,
    XboxControllerState State);

public interface IXemuGamepadProvider : IAsyncDisposable
{
    string Name { get; }
    bool IsAvailable { get; }
    bool IsReady { get; }
    void ConfigureTarget(ProcessStartInfo target);
    void ConfigureTarget(IDictionary<string, string> launchEnvironment);
    Task CreateAsync(int controllerCount, CancellationToken cancellationToken);
    Task<NativeGamepadReceipt> ApplyStateAsync(
        int controllerIndex,
        XboxControllerState state,
        CancellationToken cancellationToken);
    Task<NativeGamepadReceipt> NeutralizeAsync(CancellationToken cancellationToken);
}
