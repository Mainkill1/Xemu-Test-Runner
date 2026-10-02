namespace XemuTestRunner.Networking;

internal sealed record XisoCampaignMetricRow(string TestId, string Category, string Metric, string Unit,
    BuildStatistics A, BuildStatistics B, double[] ASamples, double[] BSamples, double ImprovementPercent,
    double AbbaImprovementPercent, double BaabImprovementPercent,
    bool OverOnePercent, bool OrderDisagreement);

internal sealed record XisoCampaignReport(string Id, string Suite, string IsoSha256, string CatalogId, string Qualification,
    string Status, int AttemptCount, int IndexedAttempts, string[] Blockers,
    XisoCampaignMetricRow[] Rows)
{
    public int ChangesOverOnePercent => Rows.Count(row => row.OverOnePercent);
    public int OrderDisagreements => Rows.Count(row => row.OrderDisagreement);
}

internal static class XisoCampaignReportBuilder
{
    private static readonly string[] BalancedLabels = ["A1", "B1", "B2", "A2", "B3", "A3", "A4", "B4"];

    public static XisoCampaignReport Build(XisoCampaignPlan plan, IReadOnlyDictionary<string, BuildRunRecord> indexed)
    {
        var blockers = new List<string>();
        var rows = new List<XisoCampaignMetricRow>();
        if (plan.Attempts.Length != plan.Chunks.Length * BalancedLabels.Length)
            blockers.Add("The campaign does not contain eight attempts per chunk.");
        foreach (var chunk in plan.Chunks)
        {
            var attempts = plan.Attempts.Where(attempt => attempt.Chunk == chunk.Index).ToArray();
            if (!attempts.Select(attempt => attempt.Label).SequenceEqual(BalancedLabels))
            {
                blockers.Add($"Chunk {chunk.Index}: ABBA/BAAB schedule is missing or out of order.");
                continue;
            }
            var records = new List<BuildRunRecord>();
            foreach (var attempt in attempts)
            {
                if (!indexed.TryGetValue(attempt.Id, out var record))
                {
                    blockers.Add($"Chunk {chunk.Index} {attempt.Label}: indexed attempt is missing.");
                    continue;
                }
                if (!record.Eligible || record.Outcome is not { Execution: "completed", Correctness: "passed", Evidence: "complete", Comparison: "eligible" })
                    blockers.Add($"Chunk {chunk.Index} {attempt.Label}: attempt is ineligible.");
                records.Add(record);
            }
            if (records.Count != BalancedLabels.Length || blockers.Count != 0) continue;
            if (records.Select(record => (record.TestKey, record.EnvironmentKey)).Distinct().Count() != 1 ||
                attempts.Select((attempt, index) => (attempt.Variant, records[index].BuildKey, records[index].Sha256))
                    .GroupBy(value => value.Variant).Any(group => group.Select(value => (value.BuildKey, value.Sha256)).Distinct().Count() != 1))
            {
                blockers.Add($"Chunk {chunk.Index}: procedure, environment or build identity differs across attempts.");
                continue;
            }
            foreach (var testId in chunk.Tests)
            {
                var prefix = "xiso/" + testId.Replace('.', '/') + "/";
                var names = records.SelectMany(record => record.Metrics)
                    .Where(metric => metric.Name.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(metric => (metric.Name, metric.Unit, metric.Direction)).Distinct().OrderBy(value => value.Name, StringComparer.Ordinal).ToArray();
                if (!names.Any(value => value.Name == prefix + "median_us"))
                {
                    blockers.Add($"Chunk {chunk.Index} {testId}: primary guest timing metric is missing.");
                    continue;
                }
                foreach (var name in names)
                {
                    var values = records.Select(record => record.Metrics.Where(metric =>
                        metric.Name == name.Name && metric.Unit == name.Unit && metric.Direction == name.Direction).ToArray()).ToArray();
                    if (values.Any(value => value.Length != 1) || name.Direction is not ("lower" or "higher"))
                    {
                        blockers.Add($"Chunk {chunk.Index} {testId}: metric {name.Name} is missing, duplicated or has no comparison direction.");
                        continue;
                    }
                    var a = new[] { values[0][0].Value, values[3][0].Value, values[5][0].Value, values[6][0].Value };
                    var b = new[] { values[1][0].Value, values[2][0].Value, values[4][0].Value, values[7][0].Value };
                    var statsA = BuildStatistics.From(a); var statsB = BuildStatistics.From(b);
                    if (statsA is null || statsB is null || statsA.Median <= 0)
                    {
                        blockers.Add($"Chunk {chunk.Index} {testId}: metric {name.Name} has an invalid or zero reference.");
                        continue;
                    }
                    double? Change(double left, double right) => left > 0 ?
                        (name.Direction == "lower" ? (left - right) / left : (right - left) / left) * 100 : null;
                    var abba = Change((a[0] + a[1]) / 2, (b[0] + b[1]) / 2);
                    var baab = Change((a[2] + a[3]) / 2, (b[2] + b[3]) / 2);
                    var overall = Change(statsA.Median, statsB.Median);
                    if (abba is null || baab is null || overall is null ||
                        !double.IsFinite(abba.Value) || !double.IsFinite(baab.Value) || !double.IsFinite(overall.Value))
                    {
                        blockers.Add($"Chunk {chunk.Index} {testId}: metric {name.Name} cannot be compared numerically.");
                        continue;
                    }
                    var conflict = abba > 1 && baab < -1 || abba < -1 && baab > 1;
                    rows.Add(new(testId, chunk.Categories.FirstOrDefault() ?? "other", name.Name[prefix.Length..], name.Unit,
                        statsA, statsB, a, b, Math.Round(overall.Value, 6), Math.Round(abba.Value, 6), Math.Round(baab.Value, 6),
                        Math.Abs(overall.Value) > 1, conflict));
                }
            }
            var selectedPrefixes = chunk.Tests.Select(test => "xiso/" + test.Replace('.', '/') + "/").ToArray();
            if (records.Any(record => record.Metrics.Any(metric => metric.Name.StartsWith("xiso/", StringComparison.Ordinal) &&
                !selectedPrefixes.Any(prefix => metric.Name.StartsWith(prefix, StringComparison.Ordinal)))))
                blockers.Add($"Chunk {chunk.Index}: an indexed result contains an unselected XISO leaf.");
        }
        if (blockers.Count > 0) rows.Clear();
        var status = blockers.Count > 0 ? "ineligible" : rows.Any(row => row.OrderDisagreement) ? "inconclusive" : "eligible";
        return new(plan.Id, plan.Suite, plan.IsoSha256, plan.CatalogId, plan.Qualification, status, plan.Attempts.Length,
            plan.Attempts.Count(attempt => indexed.ContainsKey(attempt.Id)), blockers.ToArray(), rows.ToArray());
    }
}
