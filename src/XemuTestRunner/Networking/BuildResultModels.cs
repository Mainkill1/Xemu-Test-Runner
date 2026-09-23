namespace XemuTestRunner.Networking;

internal sealed record BuildMetric(string Name, double Value, string Unit, string Direction);
internal sealed record BuildRunRecord(string RunId, string Sha256, string TestKey, string EnvironmentKey,
    string BuildKey, string Test, AgentOutcome Outcome, bool Eligible,
    IReadOnlyList<string> Issues, IReadOnlyList<BuildMetric> Metrics, string RawCsv);
internal sealed record BuildBaseline(string Sha256, string Revision, DateTimeOffset SavedUtc, IReadOnlyList<BuildRunRecord> Runs);
internal sealed record BuildDelta(string Test, string TestKey, string Context, string Metric, string Unit,
    int NA, int NB, double? A, double? B, double? ChangePercent, string Verdict);
internal sealed record BuildComparison(string A, string B, bool BaselinePinned, string? BaselineRevision,
    int RunsA, int RunsB, int BlockedRuns, string Status, string Statistic, string RecordSet,
    IReadOnlyList<BuildDelta> Rows, int MoreRows, string Csv);
internal sealed record BuildRunBrief(string RunId, string Test, AgentOutcome Outcome, bool Eligible,
    IReadOnlyList<BuildMetric> Metrics, int MoreMetrics, IReadOnlyList<string> Issues, string RawCsv)
{
    // Presentation link only; persisted build records/baseline identities stay unchanged.
    public string Diagnostics => AgentAssessmentReader.DiagnosticsUrl(RunId);
}
internal sealed record BuildSummary(string Sha256, int RunCount, int EligibleRuns, string RecordSet,
    string BaselineStatus, BuildComparison? Comparison, IReadOnlyList<BuildRunBrief> Runs, int MoreRuns, string Details);
