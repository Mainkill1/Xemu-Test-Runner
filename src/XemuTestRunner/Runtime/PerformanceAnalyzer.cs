using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using XemuTestRunner.Config;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Runtime;

public sealed record SampleDistribution(int Count, double Mean, double Median, double Min, double Max, double P95, double P99);
public sealed record MonitoringAnalysis(string Segment, int Rows, int MissingCpuSamples, int MissingDutySamples,
    SampleDistribution Cpu, SampleDistribution CollectorDuty, long Overruns);
public sealed record FlipAnalysis(int Samples, long Frames, long ElapsedUs, double CadenceFps);
public sealed record FrameAnalysis(long WindowStartUs, long WindowEndUs, long FirstSampleUs, long LastSampleUs,
    int ZeroIntervals, SampleDistribution IntervalsMs);
public sealed record PerformanceReport(int SchemaVersion, string ProfileSha256, PerformanceAnalysisDefinition Profile,
    MonitoringAnalysis? Monitoring, FlipAnalysis? Flips, FrameAnalysis? Frames,
    IReadOnlyList<PerformanceSourceReceipt> Sources, IReadOnlyList<string> Errors)
{
    public bool Complete => Errors.Count == 0;
}
public sealed record PerformanceEvaluation(PerformanceReport Report, IReadOnlyList<AssessmentCheck> Checks,
    IReadOnlyList<ReportedMeasurement> Measurements);

/// <summary>Post-exit analysis of already-collected artifacts; never a live sampler or an API-side raw scan.</summary>
public static class PerformanceAnalyzer
{
    private const int MaximumSamples = 250000;
    private static readonly Regex FlipLine = new(@"\Aelapsed_us=(\d+) frames=(\d+) fps=([0-9.]+)\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex FrameLine = new(@"\Atimestamp_us=(\d+) frame=(\d+) delta_us=(\d+)\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static PerformanceEvaluation Evaluate(PerformanceAnalysisDefinition definition, string resultDirectory, CancellationToken ct)
    {
        definition.Validate();
        var receipts = new List<PerformanceSourceReceipt>();
        var errors = new List<string>();
        var checks = new List<AssessmentCheck>();
        var measurements = new List<ReportedMeasurement>();
        MonitoringAnalysis? monitoring = null;
        FlipAnalysis? flips = null;
        FrameAnalysis? frames = null;

        void Source(string name, Action analyze)
        {
            try { ct.ThrowIfCancellationRequested(); analyze(); checks.Add(new("analysis:" + name, true, "evidence", "Complete bounded source analysis; see performance.json for windows and SHA-256.")); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or OverflowException or ArgumentException)
            {
                var message = name + ": " + error.Message;
                if (message.Length > 240) message = message[..240];
                errors.Add(message);
                checks.Add(new("analysis:" + name, false, "evidence", message));
            }
        }
        Source("monitor", () =>
        {
            using var source = new PerformanceSource(resultDirectory, definition.MetricsPath, ct);
            using var rows = source.CsvRows().GetEnumerator();
            if (!rows.MoveNext()) throw new InvalidDataException("Metrics CSV is empty.");
            var header = rows.Current;
            int Column(string name)
            {
                var matches = header.Select((value, index) => (value, index)).Where(x => x.value == name).ToArray();
                if (matches.Length != 1) throw new InvalidDataException("Required unique CSV column missing: " + name);
                return matches[0].index;
            }
            var segment = Column("segment"); var cpuColumn = Column("process_cpu_core_pct");
            var dutyColumn = Column("collector_duty_pct"); var overrunColumn = Column("overrun");
            var cpu = new List<double>(); var duty = new List<double>();
            var selected = 0; var ignored = 0; var missingCpu = 0; var missingDuty = 0; long overruns = 0;
            while (rows.MoveNext())
            {
                var row = rows.Current;
                if (row.Length == 1 && row[0].Length == 0) { ignored++; continue; }
                if (row.Length != header.Length) throw new InvalidDataException("CSV row width does not match its header.");
                if (row[segment] != definition.Segment) { ignored++; continue; }
                selected++;
                if (string.IsNullOrWhiteSpace(row[cpuColumn])) missingCpu++; else Add(cpu, Number(row[cpuColumn]));
                if (string.IsNullOrWhiteSpace(row[dutyColumn])) missingDuty++; else Add(duty, Number(row[dutyColumn]));
                if (row[overrunColumn] is not ("0" or "1")) throw new InvalidDataException("Overrun must be 0 or 1.");
                overruns += row[overrunColumn] == "1" ? 1 : 0;
            }
            receipts.Add(source.Finish(ignored));
            if (cpu.Count < definition.MinimumCpuSamples || duty.Count == 0) throw new InvalidDataException("Selected segment has insufficient CPU/duty samples.");
            monitoring = new(definition.Segment, selected, missingCpu, missingDuty, Distribution(cpu), Distribution(duty), overruns);
        });
        if (definition.GuestFlipsPath is { } flipsPath) Source("flips", () =>
        {
            using var source = new PerformanceSource(resultDirectory, flipsPath, ct);
            var tail = new Queue<(long Elapsed, long Frames)>(); var ignored = 0;
            foreach (var line in source.Lines())
            {
                var match = FlipLine.Match(line);
                if (!match.Success)
                {
                    if (line.StartsWith("elapsed_us=", StringComparison.Ordinal)) throw new InvalidDataException("Malformed flip timing record.");
                    ignored++; continue;
                }
                var elapsed = Integer(match.Groups[1].Value); var count = Integer(match.Groups[2].Value);
                if (elapsed == 0 || !double.TryParse(match.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var printedFps) || !double.IsFinite(printedFps))
                    throw new InvalidDataException("Flip elapsed time must be positive and printed FPS finite.");
                tail.Enqueue((elapsed, count));
                if (tail.Count > definition.FlipTailSamples) tail.Dequeue();
            }
            receipts.Add(source.Finish(ignored));
            if (tail.Count != definition.FlipTailSamples) throw new InvalidDataException("Insufficient flip tail records.");
            var totalFrames = tail.Aggregate(0L, (sum, item) => checked(sum + item.Frames));
            var totalTime = tail.Aggregate(0L, (sum, item) => checked(sum + item.Elapsed));
            var cadence = totalFrames * 1000000.0 / totalTime;
            if (!double.IsFinite(cadence)) throw new InvalidDataException("Cadence is outside numeric range.");
            flips = new(tail.Count, totalFrames, totalTime, cadence);
        });
        if (definition.GuestFramesPath is { } framesPath) Source("frames", () =>
        {
            using var source = new PerformanceSource(resultDirectory, framesPath, ct);
            var tail = new Queue<(long Timestamp, long Delta)>();
            long previousTime = -1, previousFrame = -1;
            var ignored = 0;
            foreach (var line in source.Lines())
            {
                var match = FrameLine.Match(line);
                if (!match.Success)
                {
                    if (line.StartsWith("timestamp_us=", StringComparison.Ordinal)) throw new InvalidDataException("Malformed frame timing record.");
                    ignored++; continue;
                }
                var time = Integer(match.Groups[1].Value); var frame = Integer(match.Groups[2].Value); var delta = Integer(match.Groups[3].Value);
                if (time < previousTime || frame <= previousFrame) throw new InvalidDataException("Frame timestamps/counters regressed or repeated.");
                previousTime = time; previousFrame = frame;
                tail.Enqueue((time, delta));
                while (tail.Count > 0 && tail.Peek().Timestamp < time - definition.FrameTailSeconds * 1000000L) tail.Dequeue();
                if (tail.Count > MaximumSamples) throw new InvalidDataException("Frame tail exceeds 250000 samples.");
            }
            receipts.Add(source.Finish(ignored));
            var valid = tail.Where(item => item.Delta > 0).ToArray();
            if (valid.Length < definition.MinimumFrameSamples) throw new InvalidDataException("Insufficient positive frame intervals.");
            frames = new(previousTime - definition.FrameTailSeconds * 1000000L, previousTime,
                valid[0].Timestamp, valid[^1].Timestamp, tail.Count - valid.Length,
                Distribution(valid.Select(item => item.Delta / 1000.0).ToList()));
        });

        void Metric(string name, double value, string unit, string direction) =>
            measurements.Add(new(name, value, unit, direction, "result:performance.json#" + name));
        if (monitoring is not null)
        {
            Metric("monitor/cpu_mean", monitoring.Cpu.Mean, "core_pct", "lower");
            Metric("monitor/cpu_median", monitoring.Cpu.Median, "core_pct", "lower");
            Metric("monitor/cpu_samples", monitoring.Cpu.Count, "samples", "neutral");
            Metric("monitor/collector_duty_mean", monitoring.CollectorDuty.Mean, "pct", "lower");
            Metric("monitor/duty_samples", monitoring.CollectorDuty.Count, "samples", "neutral");
            Metric("monitor/overruns", monitoring.Overruns, "count", "lower");
        }
        if (flips is not null)
        {
            Metric("guest/cadence_fps", flips.CadenceFps, "fps", "higher");
            Metric("guest/flip_frames", flips.Frames, "frames", "neutral");
            Metric("guest/flip_elapsed_s", flips.ElapsedUs / 1000000.0, "s", "neutral");
        }
        if (frames is not null)
        {
            Metric("guest/interval_mean_ms", frames.IntervalsMs.Mean, "ms", "lower");
            Metric("guest/interval_p50_ms", frames.IntervalsMs.Median, "ms", "lower");
            Metric("guest/interval_p95_ms", frames.IntervalsMs.P95, "ms", "lower");
            Metric("guest/interval_p99_ms", frames.IntervalsMs.P99, "ms", "lower");
            Metric("guest/interval_samples", frames.IntervalsMs.Count, "samples", "neutral");
        }
        var profileHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(definition, ConfigLoader.JsonOptions))).ToLowerInvariant();
        var report = new PerformanceReport(1, profileHash, definition, monitoring, flips, frames, receipts, errors);
        AtomicJson.Write(Path.Combine(resultDirectory, "performance.json"), report);
        return new(report, checks, measurements);
    }

    private static void Add(List<double> values, double value)
    {
        if (values.Count >= MaximumSamples) throw new InvalidDataException("Selected metric exceeds 250000 samples.");
        values.Add(value);
    }
    private static double Number(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !double.IsFinite(value) || value < 0 || value > 1e9)
            throw new InvalidDataException("Metric must be a finite nonnegative number within its analysis range.");
        return value;
    }
    private static long Integer(string text)
    {
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 0 || value > 9007199254740991L)
            throw new InvalidDataException("Timing/counter value exceeds exact supported integer range.");
        return value;
    }
    private static SampleDistribution Distribution(List<double> values)
    {
        if (values.Count == 0) throw new InvalidDataException("No samples.");
        values.Sort();
        double Percentile(double p)
        {
            var rank = (values.Count - 1) * p; var lower = (int)Math.Floor(rank); var upper = (int)Math.Ceiling(rank);
            return values[lower] + (values[upper] - values[lower]) * (rank - lower);
        }
        // Accumulate the mean without creating an overflowing raw sum.
        double mean = 0;
        for (var i = 0; i < values.Count; i++) mean += (values[i] - mean) / (i + 1);
        return new(values.Count, mean, Percentile(0.5), values[0], values[^1], Percentile(0.95), Percentile(0.99));
    }
}
