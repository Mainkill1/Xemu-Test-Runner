using System.Text.Json.Serialization;

namespace XemuTestRunner.Runtime;

/// <summary>Analysis inputs belong to the saved test, not to an agent's after-the-fact script.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PerformanceAnalysisDefinition
{
    internal const long QemuFlipIntervalSeconds = 5;
    public string Segment { get; set; } = "";
    public string MetricsPath { get; set; } = "metrics.csv";
    public string? GuestFlipsPath { get; set; }
    public string? GuestFramesPath { get; set; }
    public int FlipTailSamples { get; set; } = 6;
    public int FrameTailSeconds { get; set; } = 30;
    public int MinimumCpuSamples { get; set; } = 1;
    public int MinimumFrameSamples { get; set; } = 1;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TimingMode { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Segment) || Segment.Length > 96 || Segment.Any(char.IsControl))
            throw new InvalidDataException("Analysis.Segment must name a saved measurement segment (1..96 characters).");
        ValidatePath(MetricsPath);
        if (GuestFlipsPath is not null) ValidatePath(GuestFlipsPath);
        if (GuestFramesPath is not null) ValidatePath(GuestFramesPath);
        if (TimingMode is not null && TimingMode != "qemu-realtime-segment-v1")
            throw new InvalidDataException("Analysis.TimingMode must be qemu-realtime-segment-v1 or absent for descriptive analysis.");
        if (TimingMode is not null && GuestFramesPath is null)
            throw new InvalidDataException("Aligned QEMU analysis requires the frame source that binds its native clock and flip origin.");
        if (FlipTailSamples is < 1 or > 10000 || FrameTailSeconds is < 1 or > 3600 ||
            MinimumCpuSamples is < 1 or > 250000 || MinimumFrameSamples is < 1 or > 250000)
            throw new InvalidDataException("Analysis windows/sample requirements exceed their supported bounds.");
        if (TimingMode is not null && GuestFlipsPath is not null &&
            FrameTailSeconds <= (FlipTailSamples + 1L) * QemuFlipIntervalSeconds)
            throw new InvalidDataException("Aligned flip groups require a window longer than (FlipTailSamples + 1) * 5 seconds for nominal phase margin; use e.g. 30s/4 groups or 55s/5 groups. Actual source coverage is still required.");
        var paths = new[] { MetricsPath, GuestFlipsPath, GuestFramesPath, TimingMode is null ? null : "segments.jsonl" }
            .Where(path => path is not null).ToArray();
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
            throw new InvalidDataException("Analysis sources must be distinct.");
    }

    private static void ValidatePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.IndexOfAny(['\\', ':']) >= 0 ||
            Path.IsPathRooted(path) || path.Any(char.IsControl) ||
            path.Split('/').Any(part => part.Length == 0 || part.StartsWith('.')) ||
            path.Equals("performance.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Analysis sources must be visible run-relative paths, not host paths or the output report.");
    }
}
