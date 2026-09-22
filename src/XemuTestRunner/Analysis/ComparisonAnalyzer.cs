using System.Text.Json;
using XemuTestRunner.Config;

namespace XemuTestRunner.Analysis;

public sealed record ComparisonMeasurement(
    string RunId,
    string Variant,
    string Name,
    double Value,
    string Unit,
    string Direction);

public sealed record IneligibleAttempt(
    string RunId,
    string Variant,
    string? Job,
    string? Status,
    IReadOnlyList<string> Reasons);

public sealed record MeasurementAggregate(
    string Variant,
    string Name,
    string Unit,
    string Direction,
    int Count,
    double Mean,
    double Minimum,
    double Maximum,
    double StandardDeviation);

public sealed record ExperimentComparison(
    string ExperimentId,
    int TotalAttempts,
    int EligibleAttempts,
    IReadOnlyList<MeasurementAggregate> Measurements,
    IReadOnlyList<IneligibleAttempt> Ineligible);

public static class ComparisonAnalyzer
{
    public static ExperimentComparison Analyze(
        string resultsRoot,
        string experimentId)
    {
        if (!Directory.Exists(resultsRoot))
            throw new DirectoryNotFoundException(
                $"Results directory does not exist: {resultsRoot}");

        var measurements =
            new List<ComparisonMeasurement>();
        var ineligible =
            new List<IneligibleAttempt>();
        var total = 0;
        var eligibleAttempts = 0;

        foreach (var directory in
                 Directory.EnumerateDirectories(resultsRoot))
        {
            var resultPath =
                Path.Combine(directory, "result.json");
            if (!File.Exists(resultPath))
                continue;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(
                    File.ReadAllText(resultPath));
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;

                if (!root.TryGetProperty(
                        "experiment",
                        out var experiment) ||
                    experiment.ValueKind !=
                        JsonValueKind.Object)
                    continue;

                var id = GetString(
                    experiment,
                    "Id");
                if (!string.Equals(
                        id,
                        experimentId,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                total++;
                var runId =
                    GetString(root, "runId") ??
                    Path.GetFileName(directory);
                var variant =
                    GetString(experiment, "Variant") ??
                    "unspecified";
                var job =
                    GetString(root, "job");
                var status =
                    GetString(root, "status");

                var eligible =
                    TryComparisonEligible(
                        root,
                        out var reasons);

                if (!eligible)
                {
                    ineligible.Add(
                        new IneligibleAttempt(
                            runId,
                            variant,
                            job,
                            status,
                            reasons));
                    continue;
                }

                eligibleAttempts++;

                if (!root.TryGetProperty(
                        "workload",
                        out var workload) ||
                    workload.ValueKind !=
                        JsonValueKind.Object ||
                    !workload.TryGetProperty(
                        "Measurements",
                        out var values) ||
                    values.ValueKind !=
                        JsonValueKind.Array)
                    continue;

                foreach (var measurement in
                         values.EnumerateArray())
                {
                    if (!measurement.TryGetProperty(
                            "Value",
                            out var valueElement) ||
                        !valueElement.TryGetDouble(
                            out var value) ||
                        !double.IsFinite(value))
                        continue;

                    var name =
                        GetString(
                            measurement,
                            "Name") ??
                        "unnamed";
                    var unit =
                        GetString(
                            measurement,
                            "Unit") ??
                        "";
                    var direction =
                        GetString(
                            measurement,
                            "Direction") ??
                        "neutral";

                    measurements.Add(
                        new ComparisonMeasurement(
                            runId,
                            variant,
                            name,
                            value,
                            unit,
                            direction));
                }
            }
        }

        var aggregates = measurements
            .GroupBy(
                value => new
                {
                    value.Variant,
                    value.Name,
                    value.Unit,
                    value.Direction
                })
            .Select(group =>
            {
                var values =
                    group.Select(item => item.Value)
                        .ToArray();
                var mean = values.Average();
                var variance = values.Length <= 1
                    ? 0
                    : values.Sum(value =>
                        Math.Pow(value - mean, 2)) /
                      (values.Length - 1);

                return new MeasurementAggregate(
                    group.Key.Variant,
                    group.Key.Name,
                    group.Key.Unit,
                    group.Key.Direction,
                    values.Length,
                    mean,
                    values.Min(),
                    values.Max(),
                    Math.Sqrt(variance));
            })
            .OrderBy(item => item.Name)
            .ThenBy(item => item.Variant)
            .ToArray();

        return new ExperimentComparison(
            experimentId,
            total,
            eligibleAttempts,
            aggregates,
            ineligible);
    }

    private static bool TryComparisonEligible(
        JsonElement root,
        out IReadOnlyList<string> reasons)
    {
        var list = new List<string>();

        if (!root.TryGetProperty(
                "assessment",
                out var assessment) ||
            assessment.ValueKind !=
                JsonValueKind.Object)
        {
            list.Add("assessment is missing");
            reasons = list;
            return false;
        }

        var comparison =
            GetString(
                assessment,
                "Comparison");

        if (!string.Equals(
                comparison,
                "eligible",
                StringComparison.OrdinalIgnoreCase))
        {
            if (assessment.TryGetProperty(
                    "ComparisonReasons",
                    out var reasonArray) &&
                reasonArray.ValueKind ==
                    JsonValueKind.Array)
            {
                list.AddRange(
                    reasonArray.EnumerateArray()
                        .Where(item =>
                            item.ValueKind ==
                            JsonValueKind.String)
                        .Select(item =>
                            item.GetString()!)
                        .Where(value =>
                            !string.IsNullOrWhiteSpace(value)));
            }

            if (list.Count == 0)
                list.Add(
                    "comparison assessment is " +
                    (comparison ?? "missing"));

            reasons = list;
            return false;
        }

        reasons = [];
        return true;
    }

    private static string? GetString(
        JsonElement element,
        string property)
    {
        return element.TryGetProperty(
                   property,
                   out var value) &&
               value.ValueKind ==
                   JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
