using System.Security.Cryptography;
using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Networking;

/// <summary>Durable, immutable per-run projections. No executable names or CSV scans are used as result identity.</summary>
internal sealed class BuildResultStore(string resultsRoot)
{
    private readonly object _gate = new();
    private const int MaximumRuns = 10000;
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);
    private string Root => Path.Combine(resultsRoot, ".build-results");
    private string BaselinePath => Path.Combine(Root, "baseline.json");

    public void Save(BuildRunRecord value)
    {
        lock (_gate)
        {
            var directory = BuildDirectory(value.Sha256);
            if (!SafeRunId(value.RunId)) throw new InvalidDataException("Invalid result run ID.");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, value.RunId + ".json");
            if (File.Exists(path))
            {
                if (Hash(Read<BuildRunRecord>(path)) != Hash(value))
                    throw new AgentRequestException(409, "result_identity_conflict", "Archived result content changed after indexing.", "Keep original evidence; index a new actual attempt rather than replacing a known result.");
                return;
            }
            AtomicJson.Write(path, value);
        }
    }

    public IReadOnlyList<BuildRunRecord> Runs(string sha256)
    {
        lock (_gate)
        {
            var directory = BuildDirectory(sha256);
            var files = Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.json").Take(MaximumRuns + 1).ToArray() : [];
            if (files.Length == 0)
                throw new AgentRequestException(404, "build_results_not_found", "No indexed results exist for this executable SHA-256.", "Wait for archival/indexing or index an existing archived run. Names cannot select a build.");
            if (files.Length > MaximumRuns) throw new InvalidDataException("Build result query exceeds 10000 attempts.");
            var values = files.Select(Read<BuildRunRecord>).OrderBy(value => value.RunId, StringComparer.Ordinal).ToArray();
            if (values.Any(value => value.Sha256 != Sha(sha256) || !SafeRunId(value.RunId) || value.Metrics is null || value.Issues is null || value.Outcome is null))
                throw new InvalidDataException("Invalid saved result record.");
            return values;
        }
    }

    public object Pin(string sha256)
    {
        lock (_gate)
        {
            var values = Runs(sha256);
            if (values.Any(value => !value.Eligible || value.Metrics.Count == 0) ||
                values.GroupBy(value => (value.TestKey, value.EnvironmentKey)).Any(group => group.Select(value => value.BuildKey).Distinct().Count() != 1))
                throw new AgentRequestException(409, "baseline_not_qualified", "The indexed build contains ineligible, unmeasured or ambiguous attempts.", "Use a qualified known build; baseline selection never drops failed repetitions automatically.");
            var sha = Sha(sha256);
            var baseline = new BuildBaseline(sha, Hash(new { sha, values }), DateTimeOffset.UtcNow, values);
            if (JsonSerializer.SerializeToUtf8Bytes(baseline, ConfigLoader.JsonOptions).Length > 8 * 1024 * 1024)
                throw new InvalidDataException("Baseline snapshot exceeds 8 MiB.");
            CheckPath(Root);
            AtomicJson.Write(BaselinePath, baseline);
            return BaselineInfo(baseline);
        }
    }

    public object Baseline() => BaselineInfo(ReadBaseline());
    private static object BaselineInfo(BuildBaseline? value) => new
    {
        configured = value is not null, sha256 = value?.Sha256, revision = value?.Revision,
        savedUtc = value?.SavedUtc, runCount = value?.Runs.Count ?? 0
    };
    private BuildBaseline? ReadBaseline()
    {
        CheckPath(Root);
        if (!File.Exists(BaselinePath)) return null;
        var value = Read<BuildBaseline>(BaselinePath);
        if (value.Runs is null || value.Runs.Count == 0 || value.Runs.Any(run => run.Sha256 != value.Sha256 || !run.Eligible) ||
            Hash(new { sha = value.Sha256, values = value.Runs }) != value.Revision)
            throw new InvalidDataException("Saved baseline identity is invalid.");
        _ = Sha(value.Sha256);
        return value;
    }

    public BuildComparison Compare(string? a, string b, bool full = false)
    {
        lock (_gate)
        {
            var baseline = a is null ? ReadBaseline() : null;
            if (a is null && baseline is null)
                throw new AgentRequestException(409, "baseline_not_set", "No known baseline has been selected.", "PUT /api/v1/baseline with a qualified executable sha256, or supply A explicitly.");
            var left = baseline?.Runs ?? Runs(a!);
            var right = Runs(b);
            var shaA = baseline?.Sha256 ?? Sha(a!);
            var shaB = Sha(b);
            var all = new List<BuildDelta>();
            foreach (var key in left.Concat(right).Select(value => (value.TestKey, value.EnvironmentKey)).Distinct().OrderBy(key => key.TestKey).ThenBy(key => key.EnvironmentKey))
            {
                var groupA = left.Where(value => value.TestKey == key.TestKey && value.EnvironmentKey == key.EnvironmentKey).ToArray();
                var groupB = right.Where(value => value.TestKey == key.TestKey && value.EnvironmentKey == key.EnvironmentKey).ToArray();
                var label = (groupB.FirstOrDefault() ?? groupA[0]).Test;
                var failure = groupA.Length == 0 ? "missingA" : groupB.Length == 0 ? "missingB" :
                    groupA.Concat(groupB).Any(value => !value.Eligible) ? "ineligible" :
                    groupA.Select(value => value.BuildKey).Distinct().Count() != 1 || groupB.Select(value => value.BuildKey).Distinct().Count() != 1 ? "ambiguousBuild" : null;
                var metrics = groupA.Concat(groupB).SelectMany(value => value.Metrics)
                    .Select(metric => (metric.Name, metric.Unit, metric.Direction)).Distinct().OrderBy(metric => metric.Name).ThenBy(metric => metric.Unit).ToArray();
                if (metrics.Length == 0)
                    all.Add(new(Clip(label), key.TestKey[..12], key.EnvironmentKey[..12], "(none)", "", groupA.Length, groupB.Length, null, null, null, failure ?? "unmeasured"));
                foreach (var metric in metrics)
                {
                    double[] Values(IEnumerable<BuildRunRecord> group) => group.SelectMany(value => value.Metrics)
                        .Where(value => value.Name == metric.Name && value.Unit == metric.Unit && value.Direction == metric.Direction)
                        .Select(value => value.Value).ToArray();
                    var aa = Values(groupA);
                    var bb = Values(groupB);
                    var verdict = failure ?? (aa.Length != groupA.Length || bb.Length != groupB.Length ? "missingMetric" : null);
                    var statsA = verdict is null ? BuildStatistics.From(aa) : null;
                    var statsB = verdict is null ? BuildStatistics.From(bb) : null;
                    if (verdict is null && (statsA is null || statsB is null)) verdict = "numericRange";
                    double? va = verdict is null ? statsA!.Median : null;
                    double? vb = verdict is null ? statsB!.Median : null;
                    double? percent = null;
                    if (verdict is null)
                    {
                        if (va == 0) verdict = "zeroBaseline";
                        else
                        {
                            var change = (vb!.Value / va!.Value - 1) * 100;
                            if (!double.IsFinite(change)) verdict = "numericRange";
                            else
                            {
                                percent = Math.Round(change, 6);
                                verdict = vb == va ? "unchanged" : metric.Direction == "neutral" ? "changed" :
                                    (metric.Direction == "lower" ? vb < va : vb > va) ? "improved" : "regressed";
                            }
                        }
                    }
                    // Preserve full metric identity even when the readable label
                    // is shortened by a renderer. Distinct leaf IDs must not collapse.
                    all.Add(new(Clip(label), key.TestKey[..12], key.EnvironmentKey[..12], metric.Name, metric.Unit,
                        aa.Length, bb.Length, va, vb, percent, verdict!, metric.Direction, statsA, statsB));
                }
            }
            var measured = all.Count(row => row.ChangePercent.HasValue);
            var status = measured == 0 ? "incomparable" : measured == all.Count ? "comparable" : "partial";
            var endpoint = "/api/v1/compare?" + (a is null ? "" : "A=" + shaA + "&") + "B=" + shaB + "&format=csv";
            var result = new BuildComparison(shaA, shaB, baseline is not null, baseline?.Revision,
                left.Count, right.Count, left.Concat(right).Count(value => !value.Eligible), status,
                "medianOfAttempts", Hash(new { left, right }), all, 0, endpoint);
            return full ? result : Bound(result);
        }
    }

    public BuildSummary Summary(string sha256)
    {
        lock (_gate)
        {
            var values = Runs(sha256);
            var baseline = ReadBaseline();
            var comparison = baseline is null ? null : Compare(null, sha256);
            if (comparison is not null) comparison = comparison with { Rows = comparison.Rows.Take(3).ToArray(), MoreRows = comparison.MoreRows + Math.Max(0, comparison.Rows.Count - 3) };
            var briefs = values.Take(3).Select(value => new BuildRunBrief(value.RunId, Clip(value.Test), value.Outcome, value.Eligible,
                value.Metrics.Take(3).Select(metric => metric with { Name = Clip(metric.Name), Unit = Clip(metric.Unit) }).ToArray(),
                Math.Max(0, value.Metrics.Count - 3), value.Issues.Take(2).Select(Clip).ToArray(), value.RawCsv)).ToArray();
            var result = new BuildSummary(Sha(sha256), values.Count, values.Count(value => value.Eligible), Hash(values),
                baseline is null ? "notSet" : "pinned", comparison, briefs, values.Count - briefs.Length,
                "/api/v1/build-results/" + Sha(sha256) + "/runs");
            while (JsonSerializer.SerializeToUtf8Bytes(result, WireJson).Length > 4096)
            {
                if (result.Runs.Count > 0) result = result with { Runs = result.Runs.SkipLast(1).ToArray(), MoreRuns = result.MoreRuns + 1 };
                else if (result.Comparison is { Rows.Count: > 0 } c)
                    result = result with { Comparison = c with { Rows = c.Rows.SkipLast(1).ToArray(), MoreRows = c.MoreRows + 1 } };
                else break;
            }
            return result;
        }
    }

    public static BuildComparison Bound(BuildComparison value)
    {
        var kept = value.Rows.Take(8).ToArray();
        value = value with { Rows = kept, MoreRows = value.MoreRows + Math.Max(0, value.Rows.Count - kept.Length) };
        while (JsonSerializer.SerializeToUtf8Bytes(value, WireJson).Length > 4096 && value.Rows.Count > 0)
            value = value with { Rows = value.Rows.SkipLast(1).ToArray(), MoreRows = value.MoreRows + 1 };
        return value;
    }
    internal static string Clip(string text) => text.Length > 48 ? text[..45] + "..." : text;
    internal static string Sha(string? text)
    {
        if (text is null || text.Length != 64 || !text.All(Uri.IsHexDigit)) throw new InvalidDataException("A complete executable SHA-256 is required.");
        return text.ToLowerInvariant();
    }
    private string BuildDirectory(string sha256)
    {
        CheckPath(Root);
        var value = Path.Combine(Root, Sha(sha256));
        CheckPath(value);
        return value;
    }
    internal static bool SafeRunId(string value) => value.Length is >= 1 and <= 128 && value[0] != '.' && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    internal static void CheckPath(string path)
    {
        if ((Directory.Exists(path) || File.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked result store paths are not supported.");
    }
    internal static T Read<T>(string path)
    {
        CheckPath(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length > 8 * 1024 * 1024) throw new InvalidDataException("Result metadata exceeds 8 MiB.");
        return JsonSerializer.Deserialize<T>(file, ConfigLoader.JsonOptions) ?? throw new InvalidDataException("Empty result metadata.");
    }
    internal static string Hash(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, ConfigLoader.JsonOptions))).ToLowerInvariant();
}
