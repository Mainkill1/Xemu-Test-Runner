using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Diagnostics;

namespace XemuTestRunner.Queue;

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
    public string LaunchMode { get; set; } = "direct";
    public string? SnapshotName { get; set; }
    public bool StartPaused { get; set; }
    public bool RequireInput { get; set; }
    public List<DiagnosticRecipe> Diagnostics { get; set; } = [];

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
        if (job.Arguments is null || job.Environment is null || job.Plan is null || job.RequiredFiles is null ||
            job.Tags is null || job.Diagnostics is null)
            throw new InvalidDataException("Job collections cannot be null.");
        job.PackageDirectory = Path.GetFullPath(packageDirectory);
        if (job.TimeoutSeconds < 0 || job.TimeoutSeconds > int.MaxValue / 1000)
            throw new InvalidDataException("TimeoutSeconds must be between 0 and 2147483.");
        if (job.TargetOs is not null && job.TargetOs.ToLowerInvariant() is not ("windows" or "linux"))
            throw new InvalidDataException("TargetOs must be windows or linux when supplied.");
        if (job.ExpectedExecutableSha256 is not null && (job.ExpectedExecutableSha256.Length != 64 || !job.ExpectedExecutableSha256.All(Uri.IsHexDigit)))
            throw new InvalidDataException("ExpectedExecutableSha256 must be 64 hexadecimal characters.");
        if (job.LaunchMode.Trim().ToLowerInvariant() is not ("direct" or "renderdoc"))
            throw new InvalidDataException("LaunchMode must be direct or renderdoc.");
        if (job.SnapshotName is not null && (job.SnapshotName.Length is < 1 or > 200 ||
            job.SnapshotName.Any(ch => char.IsControl(ch) || ch is '\r' or '\n')))
            throw new InvalidDataException("SnapshotName contains invalid characters.");
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
        for (var stepIndex = 0; stepIndex < job.Plan.Count; stepIndex++)
        {
            var step = job.Plan[stepIndex];
            (step ?? throw new InvalidDataException("Null plan step.")).Validate(job.Id);
            if (step.Type.Equals("quit", StringComparison.OrdinalIgnoreCase) && stepIndex != job.Plan.Count - 1)
                throw new InvalidDataException("quit must be the final plan step.");
            if (step.Type.Equals("diagnostic", StringComparison.OrdinalIgnoreCase) &&
                !diagnosticIds.Contains(step.DiagnosticId!))
                throw new InvalidDataException($"Plan references unknown diagnostic '{step.DiagnosticId}'.");
        }
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
    public int DurationMs { get; set; } = 100;
    public string? Name { get; set; }
    public string? DiagnosticId { get; set; }
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
            case "screenshot": break;
            case "pause": break;
            case "resume": break;
            case "quit": break;
            case "require_input": break;
            case "diagnostic":
                if (string.IsNullOrWhiteSpace(DiagnosticId))
                    throw new InvalidDataException("diagnostic step requires DiagnosticId.");
                break;
            default: throw new InvalidDataException("Unsupported plan step: " + Type);
        }
    }
}
