using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Reliability;
using XemuTestRunner.Runtime;

namespace XemuTestRunner.Networking;

internal sealed record AgentOutcome(string Execution, string Correctness, string Evidence, string Comparison);
internal sealed record AgentFailure(string Name, string Category, string Detail);
internal sealed record AgentResultSummary(bool Ok, string RunId, bool Available, string? Code, AgentOutcome? Outcome,
    IReadOnlyList<string> Reasons, int MoreReasons, IReadOnlyList<AgentFailure> Failures,
    int MoreFailures, bool Truncated, string Detail);

/// <summary>Projects the canonical assessment, never inferring correctness from process exit or queue location.</summary>
internal sealed class AgentAssessmentReader
{
    private const long MaximumBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly Dictionary<string, (long Length, long Modified, AgentResultSummary Value)> _cache = new(StringComparer.Ordinal);

    public AgentResultSummary Read(string results, string runId)
    {
        var catalog = new EvidenceCatalog(results);
        var directory = catalog.Resolve(runId, ".");
        if (!Directory.Exists(directory))
            throw new AgentRequestException(404, "run_not_found", "No run has this ID.", "Follow the job's run link or list runs first.");
        var path = catalog.Resolve(runId, "assessment.json");
        var info = new FileInfo(path);
        if (!info.Exists) return Unavailable(runId, "assessment_missing");
        var length = info.Length;
        var modified = info.LastWriteTimeUtc.Ticks;
        lock (_gate)
        {
            if (_cache.TryGetValue(path, out var cached) && cached.Length == length && cached.Modified == modified)
                return cached.Value;
        }
        AgentResultSummary summary;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length > MaximumBytes) summary = Unavailable(runId, "assessment_too_large");
            else
            {
                using var document = JsonDocument.Parse(file);
                var root = document.RootElement;
                var required = new[] { "Execution", "Correctness", "Evidence", "Comparison", "ComparisonReasons", "Checks" };
                if (root.ValueKind != JsonValueKind.Object || required.Any(name =>
                    !root.EnumerateObject().Any(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))))
                    throw new InvalidDataException("Assessment fields are missing.");
                var assessment = root.Deserialize<RunAssessment>(ConfigLoader.JsonOptions)
                    ?? throw new InvalidDataException("Empty assessment.");
                if (!Enum.IsDefined(assessment.Execution) || !Enum.IsDefined(assessment.Correctness) ||
                    !Enum.IsDefined(assessment.Evidence) || !Enum.IsDefined(assessment.Comparison) ||
                    assessment.ComparisonReasons is null || assessment.Checks is null ||
                    assessment.ComparisonReasons.Any(value => value is null) ||
                    assessment.Checks.Any(check => check is null || check.Name is null || check.Category is null || check.Detail is null))
                    throw new InvalidDataException("Invalid assessment fields.");
                summary = Project(runId, assessment);
            }
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        { summary = Unavailable(runId, "assessment_invalid"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return Unavailable(runId, "assessment_unreadable"); }
        lock (_gate)
        {
            if (_cache.Count >= 128) _cache.Clear();
            _cache[path] = (length, modified, summary);
        }
        return summary;
    }

    private static AgentResultSummary Project(string runId, RunAssessment assessment)
    {
        var truncated = false;
        string Clip(string text, int maximum)
        {
            if (text.Length <= maximum) return text;
            truncated = true;
            return text[..maximum] + "...";
        }
        var allFailures = assessment.Checks.Where(check => !check.Passed).ToArray();
        var reasons = assessment.ComparisonReasons.Take(4).Select(reason => Clip(reason, 160)).ToList();
        var failures = allFailures.Take(4).Select(check => new AgentFailure(
            Clip(check.Name, 64), Clip(check.Category, 32), Clip(check.Detail, 160))).ToList();
        var outcome = new AgentOutcome(Name(assessment.Execution), Name(assessment.Correctness), Name(assessment.Evidence), Name(assessment.Comparison));
        AgentResultSummary Build() => new(true, runId, true, null, outcome, reasons.ToArray(),
            assessment.ComparisonReasons.Count - reasons.Count, failures.ToArray(), allFailures.Length - failures.Count,
            truncated || reasons.Count < assessment.ComparisonReasons.Count || failures.Count < allFailures.Length, Detail(runId));
        var result = Build();
        // Escaped Unicode can cost more bytes than character counts suggest.
        // Drop whole detail entries, reporting every omission, until within budget.
        while (JsonSerializer.SerializeToUtf8Bytes(result, OutputJson).Length > 4096 && (failures.Count > 0 || reasons.Count > 0))
        {
            if (failures.Count > 0) failures.RemoveAt(failures.Count - 1);
            else reasons.RemoveAt(reasons.Count - 1);
            truncated = true;
            result = Build();
        }
        return result;
    }

    private static string Name<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
    private static string Detail(string runId) => "/api/v1/runs/" + Uri.EscapeDataString(runId) + "/artifacts/assessment.json";
    private static AgentResultSummary Unavailable(string runId, string code) => new(true, runId, false, code, null, [], 0, [], 0, false, Detail(runId));
}
