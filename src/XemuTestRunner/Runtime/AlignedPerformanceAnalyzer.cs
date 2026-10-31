using System.Globalization;
using System.Text.Json;
using XemuTestRunner.Config;

namespace XemuTestRunner.Runtime;

public sealed record AlignedFrameWindow(string ClockDomain, long SegmentStartUs, long SegmentEndUs,
    long StartUs, long EndUs, int FrameEvents, long LastEventAtOrBeforeEndUs, long FirstEventAfterEndUs);

internal static class AlignedPerformanceAnalyzer
{
    internal sealed record Analysis(FlipAnalysis? Flips, FrameAnalysis Frames, AlignedFrameWindow Window);
    private sealed record Window(string Domain, long SegmentStart, long Start, long End);
    private sealed record FlipBoundary(long Count, long Time, long Frames, long Elapsed);

    internal static Analysis Analyze(PerformanceAnalysisDefinition definition, string directory,
        List<PerformanceSourceReceipt> receipts, CancellationToken ct)
    {
        var window = ReadWindow(definition, directory, receipts, ct);
        var flips = ReadFlips(definition, directory, receipts, ct);
        using var source = new PerformanceSource(directory, definition.GuestFramesPath!, ct);
        long firstTime = -1, previousTime = -1, previousFrame = 0, lastBeforeEnd = -1, firstAfterEnd = -1;
        long previousFlipTime = -1, previousFlipFrame = 0;
        var positive = new List<double>();
        long firstIncluded = -1, lastIncluded = -1;
        var tail = new Queue<(long Start, long End, long Frames, long Elapsed)>();
        var ignored = 0; var events = 0; var zero = 0; var nextFlip = 0; var leading = false;
        foreach (var line in source.Lines())
        {
            var match = PerformanceAnalyzer.FrameLine.Match(line);
            if (!match.Success)
            {
                if (line.StartsWith("timestamp_us=", StringComparison.Ordinal))
                    throw new InvalidDataException("Malformed aligned frame record.");
                ignored++; continue;
            }
            var time = PerformanceAnalyzer.Integer(match.Groups[1].Value);
            var frame = PerformanceAnalyzer.Integer(match.Groups[2].Value);
            var delta = PerformanceAnalyzer.Integer(match.Groups[3].Value);
            if (firstTime < 0)
            {
                if (frame != 1 || delta != 0)
                    throw new InvalidDataException("QEMU source origin requires frame1 with delta0, not a truncated log.");
                firstTime = time;
                previousFlipTime = time;
            }
            else if (time < previousTime || frame != previousFrame + 1 || delta != time - previousTime)
                throw new InvalidDataException("Aligned frame clock, counter or interval is inconsistent.");
            previousTime = time; previousFrame = frame;
            if (time <= window.Start) leading = true;
            if (time <= window.End) lastBeforeEnd = time;
            else if (firstAfterEnd < 0) firstAfterEnd = time;
            if (time > window.Start && time <= window.End)
            {
                if (++events > PerformanceAnalyzer.MaximumSamples)
                    throw new InvalidDataException("Aligned frame window exceeds 250000 samples.");
                if (delta == 0) zero++;
                else
                {
                    positive.Add(delta / 1000.0);
                    if (firstIncluded < 0) firstIncluded = time;
                    lastIncluded = time;
                }
            }
            if (definition.GuestFlipsPath is not null &&
                time - previousFlipTime >= PerformanceAnalysisDefinition.QemuFlipIntervalSeconds * 1_000_000)
            {
                if (nextFlip >= flips.Count)
                    throw new InvalidDataException("A producer flip group is missing, including a possible truncated suffix.");
                var flip = flips[nextFlip++];
                // Both loggers are initialized by one nv2a_profile_log_increment(now) call.
                // Matching every group to its frame counter proves the declared common origin.
                if (flip.Count != frame || flip.Frames != frame - previousFlipFrame ||
                    flip.Elapsed != time - previousFlipTime || checked(firstTime + flip.Time) != time)
                    throw new InvalidDataException("Flip elapsed time does not match its co-timed frame origin.");
                previousFlipTime = time; previousFlipFrame = frame;
                var start = time - flip.Elapsed;
                if (start >= window.Start && time <= window.End)
                {
                    tail.Enqueue((start, time, flip.Frames, flip.Elapsed));
                    if (tail.Count > definition.FlipTailSamples) tail.Dequeue();
                }
            }
        }
        receipts.Add(source.Finish(ignored));
        if (!leading || firstAfterEnd < 0 || lastBeforeEnd < 0)
            throw new InvalidDataException("Frame source does not bracket the fixed host window; progress is stale or collection ended early.");
        if (positive.Count < definition.MinimumFrameSamples)
            throw new InvalidDataException("Fixed host window has insufficient positive frame intervals.");
        if (nextFlip != flips.Count)
            throw new InvalidDataException("Flip frame counters extend beyond the retained frame source.");
        FlipAnalysis? flipAnalysis = null;
        if (definition.GuestFlipsPath is not null)
        {
            if (tail.Count != definition.FlipTailSamples)
                throw new InvalidDataException("Fixed host window contains too few complete flip groups.");
            var frames = tail.Aggregate(0L, (sum, item) => checked(sum + item.Frames));
            var elapsed = tail.Aggregate(0L, (sum, item) => checked(sum + item.Elapsed));
            flipAnalysis = new(tail.Count, frames, elapsed, frames * 1000000.0 / elapsed,
                tail.Peek().Start, tail.Last().End);
        }
        return new(flipAnalysis,
            new(window.Start, window.End, firstIncluded, lastIncluded, zero, PerformanceAnalyzer.Distribution(positive)),
            new(window.Domain, window.SegmentStart, window.End, window.Start, window.End,
                events, lastBeforeEnd, firstAfterEnd));
    }

    private static Window ReadWindow(PerformanceAnalysisDefinition definition, string directory,
        List<PerformanceSourceReceipt> receipts, CancellationToken ct)
    {
        using var source = new PerformanceSource(directory, "segments.jsonl", ct);
        MeasurementSegmentEvent? start = null, end = null;
        var ignored = 0;
        foreach (var line in source.Lines())
        {
            var marker = JsonSerializer.Deserialize<MeasurementSegmentEvent>(line, ConfigLoader.JsonOptions)
                ?? throw new InvalidDataException("Empty measurement segment marker.");
            if (marker.Name != definition.Segment) { ignored++; continue; }
            if (marker.ClockDomain is not ("linux-clock-monotonic" or "windows-qpc") ||
                marker.HostTimestampUs is not (>= 0 and <= 9_007_199_254_740_991))
                throw new InvalidDataException("Selected segment lacks a supported absolute native clock anchor.");
            if (marker.Action == "start" && start is null && end is null) start = marker;
            else if (marker.Action == "end" && start is not null && end is null) end = marker;
            else throw new InvalidDataException("Selected segment is duplicated, abandoned or out of order.");
        }
        receipts.Add(source.Finish(ignored));
        if (start is null || end is null || start.ClockDomain != end.ClockDomain)
            throw new InvalidDataException("A unique completed segment with one native clock domain is required.");
        var startUs = start.HostTimestampUs!.Value;
        var endUs = end.HostTimestampUs!.Value;
        var windowStart = endUs - definition.FrameTailSeconds * 1_000_000L;
        if (endUs <= startUs || windowStart < startUs)
            throw new InvalidDataException("The fixed frame interval does not fit inside the selected host segment.");
        return new(start.ClockDomain!, startUs, windowStart, endUs);
    }

    private static List<FlipBoundary> ReadFlips(PerformanceAnalysisDefinition definition, string directory,
        List<PerformanceSourceReceipt> receipts, CancellationToken ct)
    {
        var result = new List<FlipBoundary>();
        if (definition.GuestFlipsPath is not { } path) return result;
        using var source = new PerformanceSource(directory, path, ct);
        var ignored = 0; long frames = 0, elapsed = 0;
        foreach (var line in source.Lines())
        {
            var match = PerformanceAnalyzer.FlipLine.Match(line);
            if (!match.Success)
            {
                if (line.StartsWith("elapsed_us=", StringComparison.Ordinal))
                    throw new InvalidDataException("Malformed aligned flip record.");
                ignored++; continue;
            }
            var duration = PerformanceAnalyzer.Integer(match.Groups[1].Value);
            var count = PerformanceAnalyzer.Integer(match.Groups[2].Value);
            if (duration == 0 || count == 0 ||
                !double.TryParse(match.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) ||
                !double.IsFinite(fps)) throw new InvalidDataException("Flip duration/count must be positive and printed FPS finite.");
            if (result.Count >= PerformanceAnalyzer.MaximumSamples)
                throw new InvalidDataException("Aligned flip source exceeds 250000 groups.");
            frames = checked(frames + count); elapsed = checked(elapsed + duration);
            result.Add(new(frames, elapsed, count, duration));
        }
        receipts.Add(source.Finish(ignored));
        return result;
    }
}
