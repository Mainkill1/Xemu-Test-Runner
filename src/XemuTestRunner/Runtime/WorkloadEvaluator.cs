using System.Text.Json;
using XemuTestRunner.Queue;

namespace XemuTestRunner.Runtime;

public sealed record ReportedMeasurement(string Name, double Value, string Unit, string Direction, string Source);

public sealed record WorkloadEvaluation(
    CorrectnessOutcome Correctness,
    EvidenceOutcome Evidence,
    IReadOnlyList<AssessmentCheck> Checks,
    IReadOnlyList<ReportedMeasurement> Measurements);

/// <summary>Evaluates each declared requirement independently; one unreadable artifact must not hide the others.</summary>
public static class WorkloadEvaluator
{
    public static async Task<WorkloadEvaluation> EvaluateAsync(
        JobDefinition job,
        string packageDirectory,
        string resultDirectory,
        RuntimeMaterialization? runtime,
        int metricSamples,
        bool planCompleted,
        CancellationToken cancellationToken)
    {
        var checks = new List<AssessmentCheck>();
        var measurements = new List<ReportedMeasurement>();

        foreach (var requirement in job.Workload.CorrectnessChecks)
        {
            checks.Add(await EvaluateArtifactAsync(requirement, "correctness",
                packageDirectory, resultDirectory, runtime, cancellationToken).ConfigureAwait(false));
        }

        foreach (var requirement in job.Workload.EvidenceRequirements)
        {
            checks.Add(await EvaluateArtifactAsync(requirement, "evidence",
                packageDirectory, resultDirectory, runtime, cancellationToken).ConfigureAwait(false));
        }

        if (job.Workload.MinimumMetricSamples > 0)
        {
            checks.Add(new AssessmentCheck(
                "minimum_metric_samples", metricSamples >= job.Workload.MinimumMetricSamples, "evidence",
                $"Required >= {job.Workload.MinimumMetricSamples}; actual {metricSamples}."));
        }

        if (job.Workload.RequirePlanCompletion && job.Plan.Count > 0)
        {
            checks.Add(new AssessmentCheck("plan_completion", planCompleted, "evidence",
                planCompleted ? "The declared plan completed." : "The declared plan did not complete."));
        }

        foreach (var definition in job.Workload.ReportedMetrics)
        {
            var result = await ReadMeasurementAsync(definition,
                packageDirectory, resultDirectory, runtime, cancellationToken).ConfigureAwait(false);
            if (result.Measurement is not null)
            {
                measurements.Add(result.Measurement);
            }

            if (definition.Required)
            {
                checks.Add(new AssessmentCheck("metric:" + definition.Name,
                    result.Measurement is not null, "evidence", result.Detail));
            }
        }

        var correctnessChecks = checks.Where(check => check.Category == "correctness").ToArray();
        var evidenceChecks = checks.Where(check => check.Category == "evidence").ToArray();
        var correctness = GetCorrectness(correctnessChecks);
        var evidence = GetEvidence(evidenceChecks);
        return new WorkloadEvaluation(correctness, evidence, checks, measurements);
    }

    private static async Task<AssessmentCheck> EvaluateArtifactAsync(
        ArtifactCheckDefinition requirement,
        string category,
        string packageDirectory,
        string resultDirectory,
        RuntimeMaterialization? runtime,
        CancellationToken cancellationToken)
    {
        var name = string.IsNullOrWhiteSpace(requirement.Name) ? requirement.Path : requirement.Name;
        try
        {
            var path = ArtifactInspector.ResolvePath(
                requirement.Scope, requirement.Path, packageDirectory, resultDirectory, runtime);
            var result = await ArtifactInspector.CheckAsync(requirement, path, cancellationToken).ConfigureAwait(false);
            return new AssessmentCheck(name, result.Passed, category, result.Detail);
        }
        catch (Exception exception) when (ArtifactInspector.IsArtifactFailure(exception))
        {
            return new AssessmentCheck(name, false, category, exception.Message);
        }
    }

    private static async Task<MeasurementRead> ReadMeasurementAsync(
        ReportedMetricDefinition definition,
        string packageDirectory,
        string resultDirectory,
        RuntimeMaterialization? runtime,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(definition.Name) || string.IsNullOrWhiteSpace(definition.Path) ||
            string.IsNullOrWhiteSpace(definition.JsonProperty))
        {
            return new(null, "Reported metric requires Name, Path, and JsonProperty.");
        }

        var direction = definition.Direction?.Trim().ToLowerInvariant();
        if (direction is not ("higher" or "lower" or "neutral"))
        {
            return new(null, $"Metric direction '{definition.Direction}' is invalid.");
        }

        try
        {
            var path = ArtifactInspector.ResolvePath(
                definition.Scope, definition.Path, packageDirectory, resultDirectory, runtime);
            using var document = await ArtifactInspector.ReadJsonAsync(path, cancellationToken).ConfigureAwait(false);
            if (!TryReadNumber(document.RootElement, definition.JsonProperty, out var value))
            {
                return new(null, $"JSON property '{definition.JsonProperty}' is missing or is not a finite number in {definition.Path}.");
            }

            return new(new ReportedMeasurement(definition.Name, value, definition.Unit, direction,
                $"{definition.Scope}:{definition.Path}#{definition.JsonProperty}"),
                "Reported metric was extracted successfully.");
        }
        catch (Exception exception) when (ArtifactInspector.IsArtifactFailure(exception))
        {
            return new(null, "Metric extraction failed: " + exception.Message);
        }
    }

    private static bool TryReadNumber(JsonElement root, string propertyPath, out double value)
    {
        value = default;
        var element = root;
        foreach (var rawPart in propertyPath.Split('.'))
        {
            var part = rawPart.Trim();
            if (part.Length == 0 || element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(part, out var next))
            {
                return false;
            }

            element = next;
        }

        // TryGetDouble still throws for non-number JSON kinds. Guard the kind first.
        return element.ValueKind == JsonValueKind.Number &&
            element.TryGetDouble(out value) && double.IsFinite(value);
    }

    private static CorrectnessOutcome GetCorrectness(IReadOnlyCollection<AssessmentCheck> checks)
    {
        if (checks.Count == 0)
        {
            return CorrectnessOutcome.NotEvaluated;
        }

        return checks.All(check => check.Passed) ? CorrectnessOutcome.Passed : CorrectnessOutcome.Failed;
    }

    private static EvidenceOutcome GetEvidence(IReadOnlyCollection<AssessmentCheck> checks)
    {
        if (checks.Count == 0)
        {
            return EvidenceOutcome.NotEvaluated;
        }

        return checks.All(check => check.Passed) ? EvidenceOutcome.Complete : EvidenceOutcome.Incomplete;
    }

    private sealed record MeasurementRead(ReportedMeasurement? Measurement, string Detail);
}
