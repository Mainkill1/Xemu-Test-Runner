namespace XemuTestRunner.Networking;

internal sealed record BuildMetric(string Name, double Value, string Unit, string Direction);
internal sealed record BuildRunRecord(string RunId, string Sha256, string TestKey, string EnvironmentKey,
    string BuildKey, string Test, AgentOutcome Outcome, bool Eligible,
    IReadOnlyList<string> Issues, IReadOnlyList<BuildMetric> Metrics, string RawCsv);
internal sealed record BuildBaseline(string Sha256, string Revision, DateTimeOffset SavedUtc, IReadOnlyList<BuildRunRecord> Runs);
internal sealed record BuildDelta(string Test, string TestKey, string Context, string Metric, string Unit,
    int NA, int NB, double? A, double? B, double? ChangePercent, string Verdict,
    string Direction = "neutral", BuildStatistics? StatsA = null, BuildStatistics? StatsB = null)
{
    public double? ImprovementPercent => A > 0 && ChangePercent.HasValue
        ? Direction == "lower" ? -ChangePercent.Value : Direction == "higher" ? ChangePercent.Value : null
        : null;
    public string Section => Metric.StartsWith("xiso/", StringComparison.Ordinal)
        ? "XISO / " + Metric.Split('/')[1] : "Reported workload metrics";
}
internal sealed record BuildComparison(string A, string B, bool BaselinePinned, string? BaselineRevision,
    int RunsA, int RunsB, int BlockedRuns, string Status, string Statistic, string RecordSet,
    IReadOnlyList<BuildDelta> Rows, int MoreRows, string Csv);
internal sealed record BuildRunBrief(string RunId, string Test, AgentOutcome Outcome, bool Eligible,
    IReadOnlyList<BuildMetric> Metrics, int MoreMetrics, IReadOnlyList<string> Issues, string RawCsv);
internal sealed record BuildSummary(string Sha256, int RunCount, int EligibleRuns, string RecordSet,
    string BaselineStatus, BuildComparison? Comparison, IReadOnlyList<BuildRunBrief> Runs, int MoreRuns, string Details);

internal sealed record BuildStatistics(int Count, double Mean, double Median, double Min, double Max, double? SampleStdDev)
{
    public static BuildStatistics? From(double[] samples)
    {
        if (samples.Length == 0 || samples.Any(value => !double.IsFinite(value))) return null;
        var values = samples.Order().ToArray();
        var count = values.Length;
        var median = count % 2 == 1 ? values[count / 2] : values[count / 2 - 1] / 2 + values[count / 2] / 2;
        var scale = values.Max(value => Math.Abs(value));
        var normalizedMean = scale == 0 ? 0 : values.Average(value => value / scale);
        var sum = values.Sum();
        // Preserve ordinary sum/count arithmetic; scale only when a finite
        // collection's sum overflows. Scaling every mean needlessly rounds 110.
        var mean = double.IsFinite(sum) ? sum / count : normalizedMean * scale;
        double? deviation = count < 2 ? null : scale == 0 ? 0 :
            Math.Sqrt(values.Sum(value => Math.Pow(value / scale - normalizedMean, 2)) / (count - 1)) * scale;
        if (deviation.HasValue && !double.IsFinite(deviation.Value)) deviation = null;
        return new(count, mean, median, values[0], values[^1], deviation);
    }
}
