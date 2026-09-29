namespace XemuTestRunner.Authoring;

public readonly record struct WindowGridPoint(int X, int Y)
{
    public const int Maximum = 10_000;

    public WindowPixelPoint ToPixel(int width, int height)
    {
        ValidateCoordinate(X, nameof(X));
        ValidateCoordinate(Y, nameof(Y));
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), width, "Window width must be greater than zero.");
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), height, "Window height must be greater than zero.");

        return new WindowPixelPoint(
            ScaleCoordinate(X, width),
            ScaleCoordinate(Y, height));
    }

    private static void ValidateCoordinate(int value, string name)
    {
        if (value is < 0 or > Maximum)
            throw new InvalidDataException($"{name} must be between 0 and {Maximum}.");
    }

    private static int ScaleCoordinate(int coordinate, int length) =>
        (int)Math.Round(
            (double)coordinate * (length - 1) / Maximum,
            MidpointRounding.AwayFromZero);
}

public readonly record struct WindowPixelPoint(int X, int Y);

public enum XemuWindowTargetRole
{
    MainWindow,
    ActiveOwnedWindow,
    ExplicitWindow
}

public sealed record XemuWindowTargetSelector(
    XemuWindowTargetRole Role,
    string? WindowId = null,
    string? TitlePattern = null)
{
    public void Validate()
    {
        if (Role == XemuWindowTargetRole.ExplicitWindow && string.IsNullOrWhiteSpace(WindowId))
            throw new InvalidDataException("ExplicitWindow requires a WindowId.");
    }
}

public sealed record XemuWindowSurface(
    int ProcessId,
    string WindowId,
    string Title,
    int ClientWidth,
    int ClientHeight,
    double DpiScale,
    long SurfaceVersion,
    bool IsVisible,
    bool IsMinimized,
    bool IsForeground);

public enum WindowPointerActionKind
{
    Move,
    Click,
    DoubleClick,
    ButtonDown,
    ButtonUp,
    Drag,
    Scroll
}

public enum WindowPointerButton
{
    None,
    Left,
    Middle,
    Right,
    X1,
    X2
}

public sealed record WindowPointerCommand(
    int ExpectedProcessId,
    string ExpectedWindowId,
    long ExpectedSurfaceVersion,
    WindowGridPoint Point,
    WindowPointerActionKind Action,
    WindowPointerButton Button,
    int HoldMs,
    int WheelDelta)
{
    public WindowPixelPoint ResolvePixel(XemuWindowSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        Validate();

        if (surface.ProcessId != ExpectedProcessId)
            throw new InvalidOperationException(
                $"Window belongs to process {surface.ProcessId}, expected {ExpectedProcessId}.");
        if (!string.Equals(surface.WindowId, ExpectedWindowId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Window ID '{surface.WindowId}' does not match expected window '{ExpectedWindowId}'.");
        if (surface.SurfaceVersion != ExpectedSurfaceVersion)
            throw new InvalidOperationException(
                $"Window surface version {surface.SurfaceVersion} does not match expected version {ExpectedSurfaceVersion}.");

        return Point.ToPixel(surface.ClientWidth, surface.ClientHeight);
    }

    public void Validate()
    {
        if (ExpectedProcessId <= 0)
            throw new InvalidDataException("ExpectedProcessId must be greater than zero.");
        if (string.IsNullOrWhiteSpace(ExpectedWindowId))
            throw new InvalidDataException("ExpectedWindowId is required.");
        if (ExpectedSurfaceVersion < 0)
            throw new InvalidDataException("ExpectedSurfaceVersion cannot be negative.");
        if (HoldMs is < 0 or > 60_000)
            throw new InvalidDataException("HoldMs must be between 0 and 60000.");
    }
}

public sealed record WindowActionResult(
    int ProcessId,
    string WindowId,
    long SurfaceVersion,
    WindowGridPoint RequestedPoint,
    WindowPixelPoint ActualPixel);
