using System.Text.Json;
using XemuTestRunner.Config;

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

    public static JobDefinition LoadPackage(string packageDirectory)
    {
        var path = Path.Combine(packageDirectory, "job.json");
        if (!File.Exists(path)) throw new InvalidDataException("Package is missing job.json.");
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("job.json exceeds 1 MiB.");
        var job = JsonSerializer.Deserialize<JobDefinition>(File.ReadAllText(path), ConfigLoader.JsonOptions)
            ?? throw new InvalidDataException("Empty job.json.");
        if (string.IsNullOrWhiteSpace(job.Id)) job.Id = Path.GetFileName(packageDirectory);
        if (string.IsNullOrWhiteSpace(job.Executable)) throw new InvalidDataException("Executable is required.");
        if (job.Arguments is null || job.Environment is null || job.Plan is null || job.RequiredFiles is null || job.Tags is null)
            throw new InvalidDataException("Job collections cannot be null.");
        if (job.TimeoutSeconds < 0 || job.TimeoutSeconds > int.MaxValue / 1000)
            throw new InvalidDataException("TimeoutSeconds must be between 0 and 2147483.");
        if (job.TargetOs is not null && job.TargetOs.ToLowerInvariant() is not ("windows" or "linux"))
            throw new InvalidDataException("TargetOs must be windows or linux when supplied.");
        if (job.ExpectedExecutableSha256 is not null && (job.ExpectedExecutableSha256.Length != 64 || !job.ExpectedExecutableSha256.All(Uri.IsHexDigit)))
            throw new InvalidDataException("ExpectedExecutableSha256 must be 64 hexadecimal characters.");
        _ = ResolveInsidePackage(packageDirectory, job.Executable);
        _ = ResolveInsidePackage(packageDirectory, job.WorkingDirectory ?? ".");
        foreach (var file in job.RequiredFiles) _ = ResolveInsidePackage(packageDirectory, file);
        foreach (var step in job.Plan) (step ?? throw new InvalidDataException("Null plan step.")).Validate(job.Id);
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
            default: throw new InvalidDataException("Unsupported plan step: " + Type);
        }
    }
}
