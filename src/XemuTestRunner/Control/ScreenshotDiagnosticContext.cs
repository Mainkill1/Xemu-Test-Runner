using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using XemuTestRunner.Runtime;

namespace XemuTestRunner.Control;

public sealed record GuestProgressContext(
    [property: JsonPropertyName("frame")] long Frame,
    [property: JsonPropertyName("timestampUs")] long TimestampUs,
    [property: JsonPropertyName("deltaUs")] long DeltaUs,
    [property: JsonPropertyName("observedHostElapsedMs")] double ObservedHostElapsedMs,
    [property: JsonPropertyName("fileAgeMs")] double? FileAgeMs);

public sealed record InputDiagnosticContext(
    [property: JsonPropertyName("button")] string Button,
    [property: JsonPropertyName("durationMs")] int DurationMs,
    [property: JsonPropertyName("hostStartedMs")] double HostStartedMs,
    [property: JsonPropertyName("hostCompletedMs")] double HostCompletedMs,
    [property: JsonPropertyName("guestBefore")] GuestProgressContext? GuestBefore,
    [property: JsonPropertyName("guestAfter")] GuestProgressContext? GuestAfter);

public sealed record SinceLastInputContext(
    [property: JsonPropertyName("hostMs")] double HostMs,
    [property: JsonPropertyName("guestUs")] long? GuestUs,
    [property: JsonPropertyName("guestFrames")] long? GuestFrames);

public sealed record ScreenshotContextDocument(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("purpose")] string Purpose,
    [property: JsonPropertyName("captured")] bool Captured,
    [property: JsonPropertyName("provider")] string? Provider,
    [property: JsonPropertyName("hostStartedMs")] double HostStartedMs,
    [property: JsonPropertyName("hostCompletedMs")] double HostCompletedMs,
    [property: JsonPropertyName("hostDurationMs")] double HostDurationMs,
    [property: JsonPropertyName("segment")] string? Segment,
    [property: JsonPropertyName("guestBefore")] GuestProgressContext? GuestBefore,
    [property: JsonPropertyName("guestAfter")] GuestProgressContext? GuestAfter,
    [property: JsonPropertyName("lastInput")] InputDiagnosticContext? LastInput,
    [property: JsonPropertyName("sinceLastInput")] SinceLastInputContext? SinceLastInput,
    [property: JsonPropertyName("guestSemantics")] string GuestSemantics,
    [property: JsonPropertyName("error")] string? Error);

/// <summary>
/// Small in-memory diagnostic context. It performs no background sampling.
/// Guest progress is read only when an input or screenshot is already occurring.
/// </summary>
public sealed class ScreenshotDiagnosticContext
{
    private const int TailBytes = 64 * 1024;
    private readonly string _resultDirectory;
    private readonly string _guestProgressPath;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly object _gate = new();
    private string? _segment;
    private InputDiagnosticContext? _lastInput;

    public ScreenshotDiagnosticContext(
        string resultDirectory,
        string? guestProgressPath = null)
    {
        _resultDirectory = Path.GetFullPath(resultDirectory);
        _guestProgressPath = string.IsNullOrWhiteSpace(guestProgressPath)
            ? "guest-frames.log"
            : guestProgressPath;
        _ = RuntimeStateManager.ResolveInside(_resultDirectory, _guestProgressPath);
    }

    public double ElapsedMs(long timestamp) =>
        Stopwatch.GetElapsedTime(_started, timestamp).TotalMilliseconds;

    public GuestProgressContext? SampleGuest()
    {
        try
        {
            var path = RuntimeStateManager.ResolveInside(
                _resultDirectory, _guestProgressPath);
            if (!File.Exists(path))
                return null;

            var info = new FileInfo(path);
            using var file = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.SequentialScan);
            if (file.Length == 0)
                return null;

            var bytes = checked((int)Math.Min(TailBytes, file.Length));
            file.Position = file.Length - bytes;
            var buffer = new byte[bytes];
            file.ReadExactly(buffer);
            var text = Encoding.UTF8.GetString(buffer);
            var lines = text.Split('\n');
            for (var index = lines.Length - 1; index >= 0; index--)
            {
                var line = lines[index].TrimEnd('\r').Trim();
                if (!TryParse(line, out var timestampUs, out var frame, out var deltaUs))
                    continue;

                var observed = Stopwatch.GetTimestamp();
                var age = (DateTime.UtcNow - info.LastWriteTimeUtc).TotalMilliseconds;
                return new GuestProgressContext(
                    frame,
                    timestampUs,
                    deltaUs,
                    ElapsedMs(observed),
                    double.IsFinite(age) ? Math.Max(0, age) : null);
            }
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or
            InvalidDataException or ArgumentException or OverflowException)
        {
            // Context is diagnostic. An unavailable live sample must never
            // become a correctness or execution failure.
        }
        return null;
    }

    public void SetSegment(string? name)
    {
        lock (_gate)
            _segment = name;
    }

    public void RecordInput(
        string button,
        int durationMs,
        long hostStarted,
        long hostCompleted,
        GuestProgressContext? guestBefore,
        GuestProgressContext? guestAfter)
    {
        lock (_gate)
        {
            _lastInput = new InputDiagnosticContext(
                button,
                durationMs,
                ElapsedMs(hostStarted),
                ElapsedMs(hostCompleted),
                guestBefore,
                guestAfter);
        }
    }

    public ScreenshotContextDocument BuildScreenshot(
        string relativeFile,
        string purpose,
        bool captured,
        string? provider,
        long hostStarted,
        long hostCompleted,
        GuestProgressContext? guestBefore,
        GuestProgressContext? guestAfter,
        string? error)
    {
        string? segment;
        InputDiagnosticContext? lastInput;
        lock (_gate)
        {
            segment = _segment;
            lastInput = _lastInput;
        }

        var startedMs = ElapsedMs(hostStarted);
        var completedMs = ElapsedMs(hostCompleted);
        SinceLastInputContext? since = null;
        if (lastInput is not null)
        {
            var point = guestBefore ?? guestAfter;
            var inputPoint = lastInput.GuestAfter ?? lastInput.GuestBefore;
            long? guestUs = null;
            long? guestFrames = null;
            if (point is not null && inputPoint is not null)
            {
                var deltaUs = point.TimestampUs - inputPoint.TimestampUs;
                var deltaFrames = point.Frame - inputPoint.Frame;
                if (deltaUs >= 0) guestUs = deltaUs;
                if (deltaFrames >= 0) guestFrames = deltaFrames;
            }
            since = new SinceLastInputContext(
                Math.Max(0, startedMs - lastInput.HostCompletedMs),
                guestUs,
                guestFrames);
        }

        return new ScreenshotContextDocument(
            1,
            relativeFile.Replace('\\', '/'),
            purpose,
            captured,
            provider,
            startedMs,
            completedMs,
            Math.Max(0, completedMs - startedMs),
            segment,
            guestBefore,
            guestAfter,
            lastInput,
            since,
            "Guest samples are the nearest live guest-frames records observed around capture; they are diagnostic context, not exact screenshot synchronization.",
            error);
    }

    private static bool TryParse(
        string line,
        out long timestampUs,
        out long frame,
        out long deltaUs)
    {
        timestampUs = frame = deltaUs = 0;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3 &&
               Try(parts[0], "timestamp_us=", out timestampUs) &&
               Try(parts[1], "frame=", out frame) &&
               Try(parts[2], "delta_us=", out deltaUs);

        static bool Try(string value, string prefix, out long parsed)
        {
            parsed = 0;
            return value.StartsWith(prefix, StringComparison.Ordinal) &&
                   long.TryParse(
                       value.AsSpan(prefix.Length),
                       System.Globalization.NumberStyles.None,
                       System.Globalization.CultureInfo.InvariantCulture,
                       out parsed) &&
                   parsed >= 0 &&
                   parsed <= 9007199254740991L;
        }
    }
}
