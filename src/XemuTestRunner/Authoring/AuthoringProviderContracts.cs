namespace XemuTestRunner.Authoring;

public sealed record FrameProgressSnapshot(
    int ProcessId,
    long PresentedFrames,
    long LastFrameTimestampUs,
    long SurfaceVersion);

public sealed record CaptureHealthSnapshot(
    int ProcessId,
    long SurfaceVersion,
    int Width,
    int Height,
    long CapturedFrames,
    double CaptureFramesPerSecond,
    long LastFrameTimestampUs,
    bool Qualified);

public sealed record AuthoringMediaProfile(
    int Width,
    int Height,
    int FramesPerSecond,
    int MinimumReadyFramesPerSecond,
    string Codec);

public sealed record AuthoringMediaHealthSnapshot(
    double EncodeFramesPerSecond,
    double DecodeFramesPerSecond,
    long EncodedFrames,
    long DecodedFrames,
    int RoundTripMilliseconds,
    double PacketLossPercent,
    bool Qualified);

public interface IXemuGamepadProvider : IAsyncDisposable
{
    string Name { get; }
    bool IsAvailable { get; }

    Task CreateAsync(int controllerCount, CancellationToken cancellationToken);

    Task ApplyStateAsync(
        int controllerIndex,
        XboxControllerState state,
        CancellationToken cancellationToken);

    Task NeutralizeAsync(CancellationToken cancellationToken);
}

public interface IXemuFrameObserver : IAsyncDisposable
{
    string Name { get; }
    bool IsAvailable { get; }

    Task StartAsync(int processId, CancellationToken cancellationToken);

    ValueTask<FrameProgressSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken);
}

public interface IXemuCaptureProvider : IAsyncDisposable
{
    string Name { get; }
    bool IsAvailable { get; }

    Task StartAsync(int processId, CancellationToken cancellationToken);

    ValueTask<CaptureHealthSnapshot> GetHealthAsync(
        CancellationToken cancellationToken);
}

public interface IAuthoringMediaSession : IAsyncDisposable
{
    string Name { get; }
    bool IsAvailable { get; }

    Task StartAsync(
        AuthoringMediaProfile profile,
        CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    ValueTask<AuthoringMediaHealthSnapshot> GetHealthAsync(
        CancellationToken cancellationToken);
}

public interface IXemuWindowInputProvider : IAsyncDisposable
{
    string Name { get; }
    bool IsAvailable { get; }

    Task<IReadOnlyList<XemuWindowSurface>> EnumerateOwnedSurfacesAsync(
        int processId,
        CancellationToken cancellationToken);

    Task<WindowActionResult> ApplyPointerAsync(
        XemuWindowSurface surface,
        WindowPointerCommand command,
        CancellationToken cancellationToken);

    Task ApplyKeyChordAsync(
        XemuWindowSurface surface,
        IReadOnlyList<string> keys,
        int holdMs,
        CancellationToken cancellationToken);

    Task NeutralizeAsync(CancellationToken cancellationToken);
}
