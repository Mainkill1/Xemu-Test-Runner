using System.Security.Cryptography;
using System.Text.Json;
using XemuTestRunner.Queue;

namespace XemuTestRunner.Runtime;

public sealed record ReportedMeasurement(
    string Name,
    double Value,
    string Unit,
    string Direction,
    string Source);

public sealed record WorkloadEvaluation(
    CorrectnessOutcome Correctness,
    EvidenceOutcome Evidence,
    IReadOnlyList<AssessmentCheck> Checks,
    IReadOnlyList<ReportedMeasurement> Measurements);

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

        foreach (var definition in
                 job.Workload.CorrectnessChecks)
        {
            checks.Add(await EvaluateArtifactAsync(
                definition,
                "correctness",
                packageDirectory,
                resultDirectory,
                runtime,
                cancellationToken).ConfigureAwait(false));
        }

        foreach (var definition in
                 job.Workload.EvidenceRequirements)
        {
            checks.Add(await EvaluateArtifactAsync(
                definition,
                "evidence",
                packageDirectory,
                resultDirectory,
                runtime,
                cancellationToken).ConfigureAwait(false));
        }

        if (job.Workload.MinimumMetricSamples > 0)
        {
            checks.Add(new AssessmentCheck(
                "minimum_metric_samples",
                metricSamples >=
                    job.Workload.MinimumMetricSamples,
                "evidence",
                $"Required >= {job.Workload.MinimumMetricSamples}; actual {metricSamples}."));
        }

        if (job.Workload.RequirePlanCompletion &&
            job.Plan.Count > 0)
        {
            checks.Add(new AssessmentCheck(
                "plan_completion",
                planCompleted,
                "evidence",
                planCompleted
                    ? "The declared plan completed."
                    : "The declared plan did not complete."));
        }

        foreach (var metric in
                 job.Workload.ReportedMetrics)
        {
            var measured =
                await TryReadReportedMetricAsync(
                    metric,
                    packageDirectory,
                    resultDirectory,
                    runtime,
                    cancellationToken)
                .ConfigureAwait(false);

            if (measured.Measurement is not null)
                measurements.Add(measured.Measurement);

            if (metric.Required)
            {
                checks.Add(new AssessmentCheck(
                    "metric:" + metric.Name,
                    measured.Measurement is not null,
                    "evidence",
                    measured.Detail));
            }
        }

        var correctnessChecks = checks
            .Where(check =>
                check.Category == "correctness")
            .ToArray();
        var evidenceChecks = checks
            .Where(check =>
                check.Category == "evidence")
            .ToArray();

        var correctness =
            correctnessChecks.Length == 0
                ? CorrectnessOutcome.NotEvaluated
                : correctnessChecks.All(
                    check => check.Passed)
                    ? CorrectnessOutcome.Passed
                    : CorrectnessOutcome.Failed;

        var evidence =
            evidenceChecks.Length == 0
                ? EvidenceOutcome.NotEvaluated
                : evidenceChecks.All(
                    check => check.Passed)
                    ? EvidenceOutcome.Complete
                    : EvidenceOutcome.Incomplete;

        return new WorkloadEvaluation(
            correctness,
            evidence,
            checks,
            measurements);
    }

    private static async Task<AssessmentCheck>
        EvaluateArtifactAsync(
            ArtifactCheckDefinition definition,
            string category,
            string packageDirectory,
            string resultDirectory,
            RuntimeMaterialization? runtime,
            CancellationToken cancellationToken)
    {
        var name = string.IsNullOrWhiteSpace(
            definition.Name)
            ? definition.Path
            : definition.Name;

        string path;
        try
        {
            path = ResolveArtifact(
                definition,
                packageDirectory,
                resultDirectory,
                runtime);
        }
        catch (Exception ex)
        {
            return new AssessmentCheck(
                name,
                false,
                category,
                ex.Message);
        }

        var exists = File.Exists(path);
        if (!exists)
        {
            return new AssessmentCheck(
                name,
                !definition.MustExist,
                category,
                definition.MustExist
                    ? $"Required artifact is missing: {path}"
                    : $"Optional artifact is absent: {path}");
        }

        var info = new FileInfo(path);
        if (definition.MinimumBytes > 0 &&
            info.Length < definition.MinimumBytes)
        {
            return new AssessmentCheck(
                name,
                false,
                category,
                $"Artifact has {info.Length} bytes; expected at least {definition.MinimumBytes}.");
        }

        if (!string.IsNullOrWhiteSpace(
                definition.ExpectedSha256))
        {
            var hash = await HashAsync(
                path,
                cancellationToken).ConfigureAwait(false);

            if (!hash.Equals(
                    definition.ExpectedSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new AssessmentCheck(
                    name,
                    false,
                    category,
                    $"SHA-256 mismatch. Expected {definition.ExpectedSha256}; actual {hash}.");
            }
        }

        if (!string.IsNullOrEmpty(
                definition.ContainsText) ||
            definition.EqualsText is not null)
        {
            if (info.Length > 16 * 1024 * 1024)
            {
                return new AssessmentCheck(
                    name,
                    false,
                    category,
                    "Text assertion refused because the artifact exceeds 16 MiB.");
            }

            var text = await File.ReadAllTextAsync(
                path,
                cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(
                    definition.ContainsText) &&
                !text.Contains(
                    definition.ContainsText,
                    StringComparison.Ordinal))
            {
                return new AssessmentCheck(
                    name,
                    false,
                    category,
                    $"Artifact does not contain required text '{definition.ContainsText}'.");
            }

            if (definition.EqualsText is not null &&
                !string.Equals(
                    text,
                    definition.EqualsText,
                    StringComparison.Ordinal))
            {
                return new AssessmentCheck(
                    name,
                    false,
                    category,
                    "Artifact text does not exactly match the declared value.");
            }
        }

        return new AssessmentCheck(
            name,
            true,
            category,
            $"{definition.Scope}:{definition.Path} satisfied the declared requirement.");
    }

    private static async Task<(
        ReportedMeasurement? Measurement,
        string Detail)> TryReadReportedMetricAsync(
        ReportedMetricDefinition definition,
        string packageDirectory,
        string resultDirectory,
        RuntimeMaterialization? runtime,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(definition.Name) ||
            string.IsNullOrWhiteSpace(definition.Path) ||
            string.IsNullOrWhiteSpace(definition.JsonProperty))
        {
            return (
                null,
                "Reported metric requires Name, Path, and JsonProperty.");
        }

        var artifactDefinition =
            new ArtifactCheckDefinition
            {
                Scope = definition.Scope,
                Path = definition.Path
            };

        string path;
        try
        {
            path = ResolveArtifact(
                artifactDefinition,
                packageDirectory,
                resultDirectory,
                runtime);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }

        if (!File.Exists(path))
            return (
                null,
                $"Metric source is missing: {path}");

        var info = new FileInfo(path);
        if (info.Length > 16 * 1024 * 1024)
            return (
                null,
                "Metric JSON exceeds 16 MiB.");

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                128 * 1024,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan);
            using var document =
                await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var element = document.RootElement;
            foreach (var part in definition.JsonProperty
                         .Split(
                             '.',
                             StringSplitOptions.RemoveEmptyEntries |
                             StringSplitOptions.TrimEntries))
            {
                if (element.ValueKind != JsonValueKind.Object ||
                    !element.TryGetProperty(
                        part,
                        out element))
                {
                    return (
                        null,
                        $"JSON property '{definition.JsonProperty}' was not found in {definition.Path}.");
                }
            }

            if (!element.TryGetDouble(out var value) ||
                !double.IsFinite(value))
            {
                return (
                    null,
                    $"JSON property '{definition.JsonProperty}' is not a finite number.");
            }

            var direction =
                definition.Direction.Trim().ToLowerInvariant();
            if (direction is not
                ("higher" or "lower" or "neutral"))
            {
                return (
                    null,
                    $"Metric direction '{definition.Direction}' is invalid.");
            }

            return (
                new ReportedMeasurement(
                    definition.Name,
                    value,
                    definition.Unit,
                    direction,
                    $"{definition.Scope}:{definition.Path}#{definition.JsonProperty}"),
                "Reported metric was extracted successfully.");
        }
        catch (Exception ex) when (
            ex is IOException or
            JsonException or
            UnauthorizedAccessException)
        {
            return (
                null,
                "Metric extraction failed: " + ex.Message);
        }
    }

    private static string ResolveArtifact(
        ArtifactCheckDefinition definition,
        string packageDirectory,
        string resultDirectory,
        RuntimeMaterialization? runtime)
    {
        return definition.Scope
            .Trim()
            .ToLowerInvariant() switch
        {
            "result" => ResolveInside(
                resultDirectory,
                definition.Path),
            "package" =>
                JobDefinition.ResolveInsidePackage(
                    packageDirectory,
                    definition.Path),
            "runtime" => runtime is null
                ? throw new InvalidDataException(
                    "Runtime artifact requested but RuntimeState was not materialized.")
                : RuntimeStateManager.ResolveInside(
                    runtime.Directory,
                    definition.Path),
            _ => throw new InvalidDataException(
                $"Unsupported artifact scope '{definition.Scope}'.")
        };
    }

    private static string ResolveInside(
        string root,
        string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) ||
            Path.IsPathRooted(relative))
            throw new InvalidDataException(
                "Artifact paths must be non-empty and relative.");

        var canonicalRoot = Path.GetFullPath(root)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(
            Path.Combine(canonicalRoot, relative));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!full.StartsWith(
                canonicalRoot + Path.DirectorySeparatorChar,
                comparison) &&
            !string.Equals(
                full,
                canonicalRoot,
                comparison))
            throw new InvalidDataException(
                "Artifact path escapes its declared scope.");

        return full;
    }

    private static async Task<string> HashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            1024 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);

        return Convert.ToHexString(
            await SHA256.HashDataAsync(
                stream,
                cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
    }
}
