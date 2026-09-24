using System.Globalization;
using System.Text;

namespace XemuTestRunner.Networking;

internal static class BuildResultFormatting
{
    public static string Markdown(BuildComparison comparison)
    {
        var text = new StringBuilder();
        text.AppendLine($"A {comparison.A[..12]} -> B {comparison.B[..12]} | {comparison.Status}");
        text.AppendLine($"Attempts {comparison.RunsA}/{comparison.RunsB}; blocked {comparison.BlockedRuns}; median per attempt. Reference: {(comparison.BaselinePinned ? "pinned baseline" : "explicit A")}.");
        text.AppendLine("| Test/context | Metric | A | B | Change | Result |");
        text.AppendLine("|---|---|---:|---:|---:|---|");
        foreach (var row in comparison.Rows)
        {
            var line = $"| {Cell(row.Test)}/{row.Context[..6]} | {Cell(row.Metric)} ({Cell(row.Unit)}) | {Number(row.A)} | {Number(row.B)} | {(row.ChangePercent.HasValue ? Number(row.ChangePercent) + "%" : "N/A")} | {row.Verdict} |";
            text.AppendLine(line);
        }
        if (comparison.MoreRows > 0) text.AppendLine($"{comparison.MoreRows} additional rows omitted; use comparison CSV.");
        text.AppendLine("Descriptive comparison of indexed attempts; not a significance test.");
        return text.ToString();
    }

    public static string Markdown(BuildSummary summary)
    {
        var text = new StringBuilder();
        text.AppendLine($"Build {summary.Sha256[..12]} | {summary.RunCount} indexed attempts | {summary.EligibleRuns} eligible | baseline {summary.BaselineStatus}");
        if (summary.Comparison is not null) text.Append(Markdown(summary.Comparison));
        else text.AppendLine("No default baseline selected; measured results are shown without an automatic comparison.");
        foreach (var run in summary.Runs)
        {
            text.AppendLine($"{Cell(run.RunId)}: execution={run.Outcome.Execution}; correctness={run.Outcome.Correctness}; evidence={run.Outcome.Evidence}; comparison={run.Outcome.Comparison}");
            foreach (var metric in run.Metrics) text.AppendLine($"  {Cell(metric.Name)}: {Number(metric.Value)} {Cell(metric.Unit)}");
            if (run.MoreMetrics > 0) text.AppendLine($"  +{run.MoreMetrics} metrics in detailed result.");
            foreach (var issue in run.Issues) text.AppendLine("  " + Cell(issue));
            text.AppendLine("  Raw CSV: " + run.RawCsv);
        }
        if (summary.MoreRuns > 0) text.AppendLine($"{summary.MoreRuns} additional attempts: {summary.Details}");
        return text.ToString();
    }

    public static string Csv(BuildComparison comparison)
    {
        var text = new StringBuilder("test,test_key,context,metric,unit,n_a,n_b,a,b,change_percent,verdict\r\n");
        foreach (var row in comparison.Rows)
        {
            text.AppendLine(string.Join(",", Quote(row.Test), Quote(row.TestKey), Quote(row.Context), Quote(row.Metric), Quote(row.Unit),
                row.NA.ToString(CultureInfo.InvariantCulture), row.NB.ToString(CultureInfo.InvariantCulture), RawNumber(row.A), RawNumber(row.B), RawNumber(row.ChangePercent), Quote(row.Verdict)));
            if (text.Length > 8 * 1024 * 1024) throw new InvalidDataException("Comparison CSV exceeds 8 MiB; reduce the recorded comparison scope.");
        }
        return text.ToString();
    }
    private static string Number(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "N/A";
    private static string RawNumber(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "";
    private static string Cell(string value) => BuildResultStore.Clip(value).Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ');
    private static string Quote(string value)
    {
        if (value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') || value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@')) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
