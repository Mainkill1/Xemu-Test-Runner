using System.Text.Json.Serialization;

namespace XemuTestRunner.Runtime;

public sealed class RuntimeStateDefinition
{
    public bool Enabled { get; set; }
    public bool KeepOnSuccess { get; set; }
    public bool KeepOnFailure { get; set; } = true;
    public List<RuntimeFileDefinition> Files { get; set; } = [];
    public List<RuntimeDiskAssetDefinition> DiskAssets { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RunIsolationDefinition? Isolation { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XisoPlanDefinition? XisoPlan { get; set; }
}

public sealed class RuntimeFileDefinition
{
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public string? ExpectedSha256 { get; set; }
}

public sealed class RuntimeDiskAssetDefinition
{
    public string AssetId { get; set; } = "";
    public string ExpectedSha256 { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Retention { get; set; } = "deleteAfterEvidence";
}

public sealed class InputIdentityDefinition
{
    public string Path { get; set; } = "";
    public string Role { get; set; } = "input";
    public bool Hash { get; set; }
    public string? ExpectedSha256 { get; set; }
}

public sealed class WorkloadContract
{
    public List<ArtifactCheckDefinition> CorrectnessChecks { get; set; } = [];
    public List<ArtifactCheckDefinition> EvidenceRequirements { get; set; } = [];
    public List<ReportedMetricDefinition> ReportedMetrics { get; set; } = [];
    public int MinimumMetricSamples { get; set; }
    public bool RequirePlanCompletion { get; set; } = true;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GuestHddResultsDefinition? GuestHddResults { get; set; }

    private PerformanceAnalysisDefinition? _analysis;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PerformanceAnalysisDefinition? Analysis
    {
        get => _analysis;
        set { value?.Validate(); _analysis = value; }
    }
}

public sealed class ArtifactCheckDefinition
{
    public string Name { get; set; } = "";
    public string Scope { get; set; } = "result";
    public string Path { get; set; } = "";
    public bool MustExist { get; set; } = true;
    public long MinimumBytes { get; set; }
    public double? MinimumNonBlackPixelRatio { get; set; }
    public int NonBlackPixelThreshold { get; set; }
    public ImageRegionDefinition? ImageRegion { get; set; }
    public string? ExpectedSha256 { get; set; }
    public string? ContainsText { get; set; }
    public string? EqualsText { get; set; }
}

public sealed class ImageRegionDefinition
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 1;
    public double Height { get; set; } = 1;
}

public sealed class ReportedMetricDefinition
{
    public string Name { get; set; } = "";
    public string Scope { get; set; } = "result";
    public string Path { get; set; } = "";
    public string JsonProperty { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Direction { get; set; } = "neutral";
    public bool Required { get; set; } = true;
}

public sealed class ExperimentDefinition
{
    public string? Id { get; set; }
    public string? Variant { get; set; }
    public string? Reference { get; set; }
    public List<string> VariedFactors { get; set; } = [];
    public List<string> ControlledFactors { get; set; } = [];
    public bool RequireCorrectnessPass { get; set; } = true;
    public bool RequireCompleteEvidence { get; set; } = true;
    public bool AllowOperatorIntervention { get; set; }
    public bool AllowDiagnostics { get; set; }
}

public sealed class OperationPolicyDefinition
{
    public string Mode { get; set; } = "smoke";
    public bool? AllowPreview { get; set; }
    public bool? AllowManualInput { get; set; }
    public bool? AllowPauseResume { get; set; }
    public bool? AllowDiagnostics { get; set; }
    public bool? AllowBulkTransfers { get; set; }
    [JsonIgnore]
    public bool IsBenchmark => Mode.Equals("benchmark", StringComparison.OrdinalIgnoreCase);
    public bool PreviewAllowed => AllowPreview ?? !IsBenchmark;
    public bool ManualInputAllowed => AllowManualInput ?? !IsBenchmark;
    public bool PauseResumeAllowed => AllowPauseResume ?? !IsBenchmark;
    public bool DiagnosticsAllowed => AllowDiagnostics ?? !IsBenchmark;
    public bool BulkTransfersAllowed => AllowBulkTransfers ?? !IsBenchmark;
}

public sealed class MeasurementSegmentDefinition
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
}
