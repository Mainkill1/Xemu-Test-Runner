using System.Globalization;
using System.Text;

namespace XemuTestRunner.Networking;

internal static class XisoCampaignReportFormatting
{
    public static string Markdown(XisoCampaignReport report)
    {
        var text = new StringBuilder();
        text.AppendLine($"## XISO campaign {Cell(report.Id)} — {report.Status}");
        text.AppendLine();
        text.AppendLine($"Suite `{Cell(report.Suite)}` · ISO `{report.IsoSha256}` · catalog `{report.CatalogId}` · suite qualification `{Cell(report.Qualification)}`");
        text.AppendLine($"Indexed attempts: {report.IndexedAttempts}/{report.AttemptCount}. Changes above 1%: {report.ChangesOverOnePercent} metric rows. Opposite ABBA/BAAB outcomes: {report.OrderDisagreements}.");
        text.AppendLine("Improvement is positive when the candidate is faster. Each order uses two A and two B attempts; the combined value uses four of each. These are guest timings, not whole-game FPS. Attempt eligibility does not qualify a candidate suite as an approved baseline.");
        if (report.Blockers.Length > 0)
        {
            text.AppendLine(); text.AppendLine("### Comparison blockers");
            foreach (var blocker in report.Blockers) text.AppendLine("- " + Cell(blocker));
            text.AppendLine(); text.AppendLine("No numeric gains are published until every planned attempt is indexed and eligible.");
            return text.ToString();
        }
        text.AppendLine();
        text.AppendLine("### Per-leaf results");
        text.AppendLine("| Category | Test | A median (µs) | B median (µs) | Improvement | ABBA | BAAB | Changes above 1% |");
        text.AppendLine("|---|---|---:|---:|---:|---:|---:|---|");
        foreach (var group in report.Rows.GroupBy(row => (row.TestId, row.Category)))
        {
            var primary = group.FirstOrDefault(row => row.Metric == "median_us");
            if (primary is null) continue;
            var changed = group.Where(row => row.OverOnePercent).Select(row =>
                Cell(row.Metric) + " " + Percent(row.ImprovementPercent)).ToArray();
            text.AppendLine($"| {Cell(primary.Category)} | {Cell(primary.TestId)} | {Number(primary.A.Median)} | {Number(primary.B.Median)} | {Percent(primary.ImprovementPercent)} | {Percent(primary.AbbaImprovementPercent)} | {Percent(primary.BaabImprovementPercent)} | {(changed.Length == 0 ? "—" : string.Join(", ", changed))}{(group.Any(row => row.OrderDisagreement) ? " (orders disagree)" : "")} |");
        }
        text.AppendLine();
        text.AppendLine("The full CSV includes each leaf's mean, median, min, max and p95 guest metrics, plus the four-attempt A/B distributions. A changed pixel or guest timing needs separate correctness and gameplay review.");
        if (Encoding.UTF8.GetByteCount(text.ToString()) > 60_000)
            throw new InvalidDataException("Campaign Markdown exceeds one PR comment; use the full CSV and split the report by category.");
        return text.ToString();
    }

    public static string Csv(XisoCampaignReport report)
    {
        var text = new StringBuilder("test_id,category,metric,unit,a1,a2,a3,a4,b1,b2,b3,b4,a_mean,a_median,a_min,a_max,b_mean,b_median,b_min,b_max,improvement_percent,abba_improvement_percent,baab_improvement_percent,over_one_percent,order_disagreement\r\n");
        foreach (var row in report.Rows)
            text.Append(string.Join(',', Quote(row.TestId), Quote(row.Category), Quote(row.Metric), Quote(row.Unit),
                Raw(row.ASamples[0]), Raw(row.ASamples[1]), Raw(row.ASamples[2]), Raw(row.ASamples[3]),
                Raw(row.BSamples[0]), Raw(row.BSamples[1]), Raw(row.BSamples[2]), Raw(row.BSamples[3]),
                Raw(row.A.Mean), Raw(row.A.Median), Raw(row.A.Min), Raw(row.A.Max),
                Raw(row.B.Mean), Raw(row.B.Median), Raw(row.B.Min), Raw(row.B.Max),
                Raw(row.ImprovementPercent), Raw(row.AbbaImprovementPercent), Raw(row.BaabImprovementPercent),
                row.OverOnePercent ? "true" : "false", row.OrderDisagreement ? "true" : "false")).Append("\r\n");
        if (text.Length > 8 * 1024 * 1024) throw new InvalidDataException("Campaign CSV exceeds 8 MiB.");
        return text.ToString();
    }

    private static string Number(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Raw(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string Percent(double value) => (value > 0 ? "+" : "") + Number(value) + "%";
    private static string Cell(string value) => value.Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ');
    private static string Quote(string value) => '"' + value.Replace("\"", "\"\"") + '"';
}
