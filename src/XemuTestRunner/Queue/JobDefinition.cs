using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Control.Gamepad;
using XemuTestRunner.Diagnostics;
using XemuTestRunner.Runtime;

namespace XemuTestRunner.Queue;

[System.Text.Json.Serialization.JsonConverter(typeof(JobTargetKindConverter))]
public enum JobTargetKind { Xemu, Process }

// A strict contract prevents enum aliases from bypassing client capability checks.
public sealed class JobTargetKindConverter : System.Text.Json.Serialization.JsonConverter<JobTargetKind>
{
    public override JobTargetKind Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (string.Equals(value, "xemu", StringComparison.OrdinalIgnoreCase)) return JobTargetKind.Xemu;
            if (string.Equals(value, "process", StringComparison.OrdinalIgnoreCase)) return JobTargetKind.Process;
        }
        throw new JsonException("TargetKind must be the string xemu or process.");
    }

    public override void Write(Utf8JsonWriter writer, JobTargetKind value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            JobTargetKind.Xemu => "xemu", JobTargetKind.Process => "process",
            _ => throw new JsonException("Unknown TargetKind.")
        });
    }
}

public sealed class JobDefinition
{
    public string Id { get; set; } = "";
    public string Executable { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
    public string? WorkingDirectory { get; set; }
    public Dictionary<string, string> Environment { get; set; } = new(StringComparer.Ordinal);
    public int TimeoutSeconds { get; set; }
    public List<string> Tags { get; set; } = [];
    public List<JobStep> Plan { get; set; } = [];
    public string? TargetOs { get; set; }
    public string? ExpectedExecutableSha256 { get; set; }
    public List<string> RequiredFiles { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    [System.Text.Json.Serialization.JsonConverter(typeof(JobTargetKindConverter))]
    public JobTargetKind TargetKind { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsStandaloneProcess => TargetKind == JobTargetKind.Process;
    public string LaunchMode { get; set; } = "direct";
    public string? SnapshotName { get; set; }
    public bool StartPaused { get; set; }
    public bool RequireInput { get; set; }
    public List<DiagnosticRecipe> Diagnostics { get; set; } = [];
    public RuntimeStateDefinition RuntimeState { get; set; } = new();
    public List<InputIdentityDefinition> Inputs { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ControllerInputDefinition? ControllerInput { get; set; }
    public WorkloadContract Workload { get; set; } = new();
    public ExperimentDefinition Experiment { get; set; } = new();
    public OperationPolicyDefinition Operations { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public string? PackageDirectory { get; private set; }

    public static JobDefinition LoadPackage(string packageDirectory) =>
        LoadPackage(packageDirectory, Path.Combine(packageDirectory, "job.json"));

    public static JobDefinition LoadPackage(
        string packageDirectory,
        string manifestPath)
    {
        if (!File.Exists(manifestPath))
            throw new InvalidDataException("Package is missing job.json.");
        if (new FileInfo(manifestPath).Length > 1024 * 1024)
            throw new InvalidDataException("job.json exceeds 1 MiB.");
        var job = JsonSerializer.Deserialize<JobDefinition>(
            File.ReadAllText(manifestPath),
            ConfigLoader.JsonOptions)
            ?? throw new InvalidDataException("Empty job.json.");
        if (string.IsNullOrWhiteSpace(job.Id)) job.Id = Path.GetFileName(packageDirectory);
        if (string.IsNullOrWhiteSpace(job.Executable)) throw new InvalidDataException("Executable is required.");
        if (job.Arguments is null || job.Environment is null || job.Plan is null ||
            job.RequiredFiles is null || job.Tags is null || job.Diagnostics is null ||
            job.RuntimeState is null || job.RuntimeState.Files is null ||
            job.RuntimeState.DiskAssets is null || job.Inputs is null || job.Workload is null ||
            job.Workload.CorrectnessChecks is null ||
            job.Workload.EvidenceRequirements is null ||
            job.Workload.ReportedMetrics is null ||
            job.Experiment is null ||
            job.Experiment.VariedFactors is null ||
            job.Experiment.ControlledFactors is null ||
            job.Operations is null)
            throw new InvalidDataException("Job collections and contract sections cannot be null.");
        job.PackageDirectory = Path.GetFullPath(packageDirectory);
        if (job.TimeoutSeconds < 0 || job.TimeoutSeconds > int.MaxValue / 1000)
            throw new InvalidDataException("TimeoutSeconds must be between 0 and 2147483.");
        if (job.TargetOs is not null && job.TargetOs.ToLowerInvariant() is not ("windows" or "linux"))
            throw new InvalidDataException("TargetOs must be windows or linux when supplied.");
        if (job.ExpectedExecutableSha256 is not null && (job.ExpectedExecutableSha256.Length != 64 || !job.ExpectedExecutableSha256.All(Uri.IsHexDigit)))
            throw new InvalidDataException("ExpectedExecutableSha256 must be 64 hexadecimal characters.");
        if (!Enum.IsDefined(job.TargetKind))
            throw new InvalidDataException("TargetKind must be xemu or process.");
        if (job.IsStandaloneProcess &&
            (job.Plan.Count != 0 || job.StartPaused || job.RequireInput ||
             job.SnapshotName is not null || job.ControllerInput is not null ||
             !job.LaunchMode.Equals("direct", StringComparison.OrdinalIgnoreCase) ||
             job.RuntimeState.Isolation is not null || job.RuntimeState.DiskAssets.Count != 0 ||
             job.RuntimeState.Xiso is not null || job.Workload.GuestHddResults is not null ||
             job.Diagnostics.Count != 0))
            throw new InvalidDataException("Process targets require direct launch without xemu control, guest state or diagnostic recipes.");
        if (job.LaunchMode.Trim().ToLowerInvariant() is not ("direct" or "renderdoc"))
            throw new InvalidDataException("LaunchMode must be direct or renderdoc.");
        if (job.ControllerInput is not null)
        {
            job.ControllerInput.Validate();
            if (!job.RequireInput)
                throw new InvalidDataException("Native ControllerInput requires RequireInput.");
            if (!job.LaunchMode.Equals("direct", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Native ControllerInput currently requires direct launch mode.");
            if (job.RuntimeState.Isolation is null)
                throw new InvalidDataException("Native ControllerInput requires managed private xemu configuration through RuntimeState.Isolation.");
        }
        if (job.SnapshotName is not null && (job.SnapshotName.Length is < 1 or > 200 ||
            job.SnapshotName.Any(ch => char.IsControl(ch) || ch is '\r' or '\n')))
            throw new InvalidDataException("SnapshotName contains invalid characters.");
        if (job.Operations.Mode.Trim().ToLowerInvariant() is not
            ("smoke" or "benchmark" or "diagnostic" or "interactive"))
            throw new InvalidDataException(
                "Operations.Mode must be smoke, benchmark, diagnostic, or interactive.");

        var hasExperimentMetadata =
            !string.IsNullOrWhiteSpace(job.Experiment.Id) ||
            !string.IsNullOrWhiteSpace(job.Experiment.Variant) ||
            !string.IsNullOrWhiteSpace(job.Experiment.Reference) ||
            job.Experiment.VariedFactors.Count > 0 ||
            job.Experiment.ControlledFactors.Count > 0;

        if (hasExperimentMetadata)
        {
            if (string.IsNullOrWhiteSpace(job.Experiment.Id))
                throw new InvalidDataException(
                    "Experiment.Id is required when experiment metadata is declared.");
            if (string.IsNullOrWhiteSpace(job.Experiment.Variant))
                throw new InvalidDataException(
                    "Experiment.Variant is required when Experiment.Id is declared.");

            var varied = new HashSet<string>(
                job.Experiment.VariedFactors,
                StringComparer.OrdinalIgnoreCase);
            if (varied.Count != job.Experiment.VariedFactors.Count)
                throw new InvalidDataException(
                    "Experiment.VariedFactors contains duplicates.");

            var controlled = new HashSet<string>(
                job.Experiment.ControlledFactors,
                StringComparer.OrdinalIgnoreCase);
            if (controlled.Count != job.Experiment.ControlledFactors.Count)
                throw new InvalidDataException(
                    "Experiment.ControlledFactors contains duplicates.");

            var overlap = varied.Intersect(
                controlled,
                StringComparer.OrdinalIgnoreCase).ToArray();
            if (overlap.Length > 0)
                throw new InvalidDataException(
                    "Experiment factors cannot be both varied and controlled: " +
                    string.Join(", ", overlap));
        }

        var inputPaths = new HashSet<string>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        foreach (var input in job.Inputs)
        {
            if (string.IsNullOrWhiteSpace(input.Path))
                throw new InvalidDataException("Inputs require Path.");
            if (!inputPaths.Add(input.Path))
                throw new InvalidDataException(
                    $"Duplicate input identity path '{input.Path}'.");
            _ = ResolveInsidePackage(packageDirectory, input.Path);

            if (!string.IsNullOrWhiteSpace(input.ExpectedSha256) &&
                (input.ExpectedSha256.Length != 64 ||
                 !input.ExpectedSha256.All(Uri.IsHexDigit)))
                throw new InvalidDataException(
                    $"Input '{input.Path}' ExpectedSha256 must be 64 hexadecimal characters.");
        }

        var runtimeDestinations = new HashSet<string>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        foreach (var runtimeFile in job.RuntimeState.Files)
        {
            if (string.IsNullOrWhiteSpace(runtimeFile.Source) ||
                string.IsNullOrWhiteSpace(runtimeFile.Destination))
                throw new InvalidDataException(
                    "RuntimeState files require Source and Destination.");
            _ = ResolveInsidePackage(packageDirectory, runtimeFile.Source);
            if (!runtimeDestinations.Add(runtimeFile.Destination))
                throw new InvalidDataException(
                    $"Duplicate RuntimeState destination '{runtimeFile.Destination}'.");
            _ = RuntimeStateManager.ResolveInside(
                Path.Combine(packageDirectory, ".runtime-validation"),
                runtimeFile.Destination);

            if (!string.IsNullOrWhiteSpace(runtimeFile.ExpectedSha256) &&
                (runtimeFile.ExpectedSha256.Length != 64 ||
                 !runtimeFile.ExpectedSha256.All(Uri.IsHexDigit)))
                throw new InvalidDataException(
                    $"Runtime seed '{runtimeFile.Source}' ExpectedSha256 must be 64 hexadecimal characters.");
        }

        foreach (var diskAsset in job.RuntimeState.DiskAssets)
        {
            if (!DiskAssetCatalog.IsValidId(diskAsset.AssetId))
                throw new InvalidDataException(
                    "RuntimeState DiskAssets require a lowercase asset ID using letters, digits and hyphens.");
            if (string.IsNullOrWhiteSpace(diskAsset.Destination))
                throw new InvalidDataException("RuntimeState DiskAssets require Destination.");
            if (diskAsset.ExpectedSha256 is null ||
                diskAsset.ExpectedSha256.Length != 64 ||
                !diskAsset.ExpectedSha256.All(Uri.IsHexDigit))
                throw new InvalidDataException(
                    $"Disk asset '{diskAsset.AssetId}' ExpectedSha256 must be 64 hexadecimal characters.");
            if (diskAsset.Retention is not ("deleteAfterEvidence" or "keep" or "keepOnFailure"))
                throw new InvalidDataException(
                    $"Disk asset '{diskAsset.AssetId}' Retention must be deleteAfterEvidence, keep, or keepOnFailure.");
            if (!runtimeDestinations.Add(diskAsset.Destination))
                throw new InvalidDataException(
                    $"Duplicate RuntimeState destination '{diskAsset.Destination}'.");
            _ = RuntimeStateManager.ResolveInside(
                Path.Combine(packageDirectory, ".runtime-validation"),
                diskAsset.Destination);
        }

        foreach (var artifact in
                 job.Workload.CorrectnessChecks.Concat(
                     job.Workload.EvidenceRequirements))
        {
            if (string.IsNullOrWhiteSpace(artifact.Path))
                throw new InvalidDataException(
                    "Workload artifact checks require Path.");

            if (artifact.Scope.Trim().ToLowerInvariant() is not
                ("result" or "package" or "runtime"))
                throw new InvalidDataException(
                    $"Unsupported workload artifact scope '{artifact.Scope}'.");

            if (artifact.MinimumBytes < 0)
                throw new InvalidDataException(
                    "Workload artifact MinimumBytes cannot be negative.");

            if (artifact.MinimumNonBlackPixelRatio is double minimumRatio &&
                (!double.IsFinite(minimumRatio) || minimumRatio < 0 || minimumRatio > 1))
                throw new InvalidDataException(
                    "Workload artifact MinimumNonBlackPixelRatio must be between 0 and 1.");

            if (artifact.NonBlackPixelThreshold is < 0 or > 255)
                throw new InvalidDataException(
                    "Workload artifact NonBlackPixelThreshold must be between 0 and 255.");

            if (artifact.NonBlackPixelThreshold != 0 &&
                artifact.MinimumNonBlackPixelRatio is null)
                throw new InvalidDataException(
                    "Workload artifact NonBlackPixelThreshold requires MinimumNonBlackPixelRatio.");

            if ((artifact.ExpectedImageDHash is null) !=
                (artifact.MaximumImageHammingDistance is null))
                throw new InvalidDataException(
                    "Workload artifact image fingerprints require ExpectedImageDHash and MaximumImageHammingDistance together.");

            if (artifact.ExpectedImageDHash is { } imageHash &&
                (imageHash.Length != 16 || !imageHash.All(Uri.IsHexDigit)))
                throw new InvalidDataException(
                    "Workload artifact ExpectedImageDHash must be 16 hexadecimal characters.");

            if (artifact.MaximumImageHammingDistance is int distance &&
                distance is < 0 or > 64)
                throw new InvalidDataException(
                    "Workload artifact MaximumImageHammingDistance must be between 0 and 64.");

            if (artifact.ImageRegion is not null &&
                artifact.MinimumNonBlackPixelRatio is null &&
                artifact.ExpectedImageDHash is null)
                throw new InvalidDataException(
                    "Workload artifact ImageRegion requires a visible-pixel or image-fingerprint check.");

            if (artifact.ImageRegion is { } region &&
                (!double.IsFinite(region.X) || !double.IsFinite(region.Y) ||
                 !double.IsFinite(region.Width) || !double.IsFinite(region.Height) ||
                 region.X < 0 || region.Y < 0 ||
                 region.Width <= 0 || region.Height <= 0 ||
                 region.X + region.Width > 1 || region.Y + region.Height > 1))
                throw new InvalidDataException(
                    "Workload artifact ImageRegion must be a positive normalized rectangle within the image.");

            if (!string.IsNullOrWhiteSpace(artifact.ExpectedSha256) &&
                (artifact.ExpectedSha256.Length != 64 ||
                 !artifact.ExpectedSha256.All(Uri.IsHexDigit)))
                throw new InvalidDataException(
                    $"Artifact '{artifact.Path}' ExpectedSha256 must be 64 hexadecimal characters.");
        }

        if (job.Operations.IsBenchmark && job.RequireInput &&
            job.Plan.Any(step =>
                step.Type.Equals("segment_start", StringComparison.OrdinalIgnoreCase)))
        {
            static bool IsBoundedSceneCheck(ArtifactCheckDefinition check) =>
                check.Scope.Equals("result", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(check.Path) &&
                check.ExpectedImageDHash is not null &&
                check.MaximumImageHammingDistance is >= 0 and <= 16;
            var hasLiveCheckpoint = true;
            for (var index = 0; index < job.Plan.Count; index++)
            {
                if (!job.Plan[index].Type.Equals("segment_start", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (index == 0 ||
                    !job.Plan[index - 1].Type.Equals("wait_for_scene", StringComparison.OrdinalIgnoreCase) ||
                    job.Plan[index - 1].Condition is not { } liveCondition ||
                    !IsBoundedSceneCheck(liveCondition) ||
                    !job.Workload.CorrectnessChecks.Any(check =>
                        IsBoundedSceneCheck(check) &&
                        check.Path.Replace('\\', '/').Equals(
                            liveCondition.Path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
                {
                    hasLiveCheckpoint = false;
                    break;
                }
            }
            if (!hasLiveCheckpoint)
                throw new InvalidDataException(
                    "Input benchmarks require wait_for_scene immediately before every segment_start, " +
                    "plus a matching result correctness check. Both scene fingerprints must use " +
                    "MaximumImageHammingDistance no greater than 16.");
        }

        foreach (var metric in job.Workload.ReportedMetrics)
        {
            if (string.IsNullOrWhiteSpace(metric.Name) ||
                string.IsNullOrWhiteSpace(metric.Path) ||
                string.IsNullOrWhiteSpace(metric.JsonProperty))
                throw new InvalidDataException(
                    "ReportedMetrics require Name, Path, and JsonProperty.");

            if (metric.Scope.Trim().ToLowerInvariant() is not
                ("result" or "package" or "runtime"))
                throw new InvalidDataException(
                    $"Unsupported reported metric scope '{metric.Scope}'.");

            if (metric.Direction.Trim().ToLowerInvariant() is not
                ("higher" or "lower" or "neutral"))
                throw new InvalidDataException(
                    $"Reported metric '{metric.Name}' Direction must be higher, lower, or neutral.");
        }

        if (job.Workload.MinimumMetricSamples < 0)
            throw new InvalidDataException(
                "Workload.MinimumMetricSamples cannot be negative.");

        _ = ResolveInsidePackage(packageDirectory, job.Executable);
        _ = ResolveInsidePackage(packageDirectory, job.WorkingDirectory ?? ".");
        foreach (var file in job.RequiredFiles) _ = ResolveInsidePackage(packageDirectory, file);
        var diagnosticIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var diagnostic in job.Diagnostics)
        {
            if (diagnostic is null)
                throw new InvalidDataException("Null diagnostic recipe.");
            diagnostic.Validate();
            if (!diagnosticIds.Add(diagnostic.Id))
                throw new InvalidDataException($"Duplicate diagnostic recipe Id '{diagnostic.Id}'.");
            if (diagnostic.Type.Equals("symbolize", StringComparison.OrdinalIgnoreCase))
                _ = ResolveInsidePackage(packageDirectory, diagnostic.DebugFile!);
        }
        string? activeSegment = null;
        for (var stepIndex = 0; stepIndex < job.Plan.Count; stepIndex++)
        {
            var step = job.Plan[stepIndex];
            (step ?? throw new InvalidDataException("Null plan step.")).Validate(job.Id);
            if (step.Type.Equals("controller_state", StringComparison.OrdinalIgnoreCase) &&
                job.ControllerInput is null)
                throw new InvalidDataException("controller_state requires native ControllerInput.");
            if (job.ControllerInput is not null &&
                step.Type.Equals("button", StringComparison.OrdinalIgnoreCase))
                _ = ControllerButtonMap.Resolve(step.Button!);

            if (step.Type.Equals("quit", StringComparison.OrdinalIgnoreCase) &&
                stepIndex != job.Plan.Count - 1)
                throw new InvalidDataException("quit must be the final plan step.");

            if (step.Type.Equals("diagnostic", StringComparison.OrdinalIgnoreCase) &&
                !diagnosticIds.Contains(step.DiagnosticId!))
                throw new InvalidDataException(
                    $"Plan references unknown diagnostic '{step.DiagnosticId}'.");

            if (step.Type.Equals("segment_start", StringComparison.OrdinalIgnoreCase))
            {
                if (activeSegment is not null)
                    throw new InvalidDataException(
                        $"Measurement segment '{activeSegment}' is already active.");
                activeSegment = step.Name;
            }
            else if (step.Type.Equals("segment_end", StringComparison.OrdinalIgnoreCase))
            {
                if (activeSegment is null)
                    throw new InvalidDataException(
                        "segment_end has no matching segment_start.");
                if (!string.Equals(activeSegment, step.Name, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"segment_end '{step.Name}' does not match active segment '{activeSegment}'.");
                activeSegment = null;
            }
        }

        if (activeSegment is not null)
            throw new InvalidDataException(
                $"Measurement segment '{activeSegment}' was never ended.");
        return job;
    }

    public static string ResolveInsidePackage(string packageDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException("Package paths must be non-empty and relative.");
        var root = Path.GetFullPath(packageDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, comparison) && !string.Equals(full, root, comparison))
            throw new InvalidDataException("Path escapes the job package: " + relativePath);
        return full;
    }
}

public sealed class JobStep
{
    public string Type { get; set; } = "";
    public int DelayMs { get; set; }
    public string? Button { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ControllerStateDefinition? State { get; set; }
    public int DurationMs { get; set; } = 100;
    public string? Name { get; set; }
    public string? DiagnosticId { get; set; }
    public ArtifactCheckDefinition? Condition { get; set; }
    public int TimeoutMs { get; set; } = 10000;
    public int PollIntervalMs { get; set; } = 100;

    public void Validate(string jobId)
    {
        if (DelayMs < 0) throw new InvalidDataException($"Job {jobId}: negative DelayMs.");
        switch (Type?.Trim().ToLowerInvariant())
        {
            case "wait": if (DelayMs <= 0) throw new InvalidDataException("wait requires DelayMs > 0."); break;
            case "button":
                if (string.IsNullOrWhiteSpace(Button) || DurationMs is < 1 or > 60000)
                    throw new InvalidDataException("button requires Button and DurationMs between 1 and 60000.");
                break;
            case "controller_state":
                if (State is null || DelayMs != 0 || DurationMs is < 1 or > 60000)
                    throw new InvalidDataException("controller_state requires State and DurationMs between 1 and 60000.");
                _ = State.ToState();
                break;
            case "screenshot": break;
            case "pause": break;
            case "resume": break;
            case "quit": break;
            case "require_input": break;
            case "diagnostic":
                if (string.IsNullOrWhiteSpace(DiagnosticId))
                    throw new InvalidDataException("diagnostic step requires DiagnosticId.");
                break;
            case "segment_start":
            case "segment_end":
                if (string.IsNullOrWhiteSpace(Name))
                    throw new InvalidDataException($"{Type} requires Name.");
                break;
            case "wait_for_artifact":
                if (Condition is null ||
                    string.IsNullOrWhiteSpace(Condition.Path))
                    throw new InvalidDataException(
                        "wait_for_artifact requires Condition.Path.");
                if (Condition.Scope.Trim().ToLowerInvariant() is not
                    ("result" or "package" or "runtime"))
                    throw new InvalidDataException(
                        $"Unsupported wait_for_artifact scope '{Condition.Scope}'.");
                if (TimeoutMs <= 0)
                    throw new InvalidDataException(
                        "wait_for_artifact TimeoutMs must be greater than zero.");
                if (PollIntervalMs is < 25 or > 5000)
                    throw new InvalidDataException(
                        "wait_for_artifact PollIntervalMs must be between 25 and 5000.");
                break;
            case "wait_for_scene":
                if (Condition is null || string.IsNullOrWhiteSpace(Condition.Path))
                    throw new InvalidDataException("wait_for_scene requires Condition.Path.");
                if (!Condition.Scope.Equals("result", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("wait_for_scene requires a result-scoped Condition.");
                if (Condition.ExpectedImageDHash is null ||
                    Condition.ExpectedImageDHash.Length != 16 ||
                    !Condition.ExpectedImageDHash.All(Uri.IsHexDigit) ||
                    Condition.MaximumImageHammingDistance is not >= 0 and <= 16)
                    throw new InvalidDataException(
                        "wait_for_scene requires a 16-digit ExpectedImageDHash and " +
                        "MaximumImageHammingDistance no greater than 16.");
                if (TimeoutMs <= 0)
                    throw new InvalidDataException("wait_for_scene TimeoutMs must be greater than zero.");
                if (PollIntervalMs is < 25 or > 5000)
                    throw new InvalidDataException(
                        "wait_for_scene PollIntervalMs must be between 25 and 5000.");
                break;
            default: throw new InvalidDataException("Unsupported plan step: " + Type);
        }
    }
}
