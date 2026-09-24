using System.Globalization;
using System.Text;

namespace XemuTestRunner.Networking;

internal static class BuildResultFormatting
{
    public static string Markdown(BuildComparison comparison)
    {
        var text = new StringBuilder();
        text.AppendLine($"A {comparison.A[..12]} -> B {comparison.B[..12]} | {comparison.Status}");
        text.AppendLine($"Attempts {comparison.RunsA}/{comparison.RunsB}; blocked {comparison.BlockedRuns}. Reference: {(comparison.BaselinePinned ? "pinned baseline" : "explicit A")}.");
        foreach (var section in comparison.Rows.GroupBy(row => (row.Test, row.TestKey, row.Context, row.Section)))
        {
            text.AppendLine();
            text.AppendLine($"### {Cell(section.Key.Test)} / {Cell(section.Key.Section)} / {section.Key.Context}");
            text.AppendLine("| Metric | A median | B median | A mean [min,max] | B mean [min,max] | n A/B | Result | Improvement % |");
            text.AppendLine("|---|---:|---:|---:|---:|---:|---|---:|");
            foreach (var row in section)
                text.AppendLine($"| {Cell(row.Metric)} ({Cell(row.Unit)}) | {Number(row.A)} | {Number(row.B)} | {Statistics(row.StatsA)} | {Statistics(row.StatsB)} | {row.NA}/{row.NB} | {row.Verdict} | {Improvement(row.ImprovementPercent)} |");
        }
        if (comparison.MoreRows > 0) text.AppendLine($"{comparison.MoreRows} additional rows omitted; use comparison CSV.");
        text.AppendLine("Improvement: positive is better; lower=(A-B)/A, higher=(B-A)/A. Medians and distributions are across attempts, not pooled guest samples. Descriptive, not a significance test.");
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
        var text = new StringBuilder("test,test_key,context,section,metric,unit,n_a,n_b,a,b,change_percent,verdict,a_mean,a_min,a_max,a_sample_stddev,b_mean,b_min,b_max,b_sample_stddev,improvement_percent\r\n");
        foreach (var row in comparison.Rows)
        {
            text.Append(string.Join(",", Quote(row.Test), Quote(row.TestKey), Quote(row.Context), Quote(row.Section), Quote(row.Metric), Quote(row.Unit),
                row.NA.ToString(CultureInfo.InvariantCulture), row.NB.ToString(CultureInfo.InvariantCulture), RawNumber(row.A), RawNumber(row.B), RawNumber(row.ChangePercent), Quote(row.Verdict),
                RawNumber(row.StatsA?.Mean), RawNumber(row.StatsA?.Min), RawNumber(row.StatsA?.Max), RawNumber(row.StatsA?.SampleStdDev),
                RawNumber(row.StatsB?.Mean), RawNumber(row.StatsB?.Min), RawNumber(row.StatsB?.Max), RawNumber(row.StatsB?.SampleStdDev), RawNumber(row.ImprovementPercent))).Append("\r\n");
            if (text.Length > 8 * 1024 * 1024) throw new InvalidDataException("Comparison CSV exceeds 8 MiB; reduce the recorded comparison scope.");
        }
        return text.ToString();
    }
    private static string Statistics(BuildStatistics? value) => value is null ? "N/A" : $"{Number(value.Mean)} [{Number(value.Min)},{Number(value.Max)}]";
    private static string Improvement(double? value) => value is null ? "N/A" : (value > 0 ? "+" : "") + Number(value) + "%";
    private static string Number(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "N/A";
    private static string RawNumber(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "";
    private static string Cell(string value) => BuildResultStore.Clip(value).Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ');
    private static string Quote(string value)
    {
        if (value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') || value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@')) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
