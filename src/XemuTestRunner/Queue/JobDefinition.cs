using System.Text.Json;
using XemuTestRunner.Config;

namespace XemuTestRunner.Queue;

public sealed class JobDefinition
{
    public string Id { get; set; } = "";
    public string Executable { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
    public string? WorkingDirectory { get; set; }
    public Dictionary<string, string> Environment { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int TimeoutSeconds { get; set; }
    public List<string> Tags { get; set; } = [];
    public List<JobStep> Plan { get; set; } = [];

    public static JobDefinition LoadPackage(string packageDirectory)
    {
        var jobPath = Path.Combine(packageDirectory, "job.json");
        if (!File.Exists(jobPath))
            throw new InvalidDataException($"Job package is missing job.json: {packageDirectory}");

        var job = JsonSerializer.Deserialize<JobDefinition>(File.ReadAllText(jobPath), ConfigLoader.JsonOptions)
            ?? throw new InvalidDataException($"Job was empty or invalid: {jobPath}");

        if (string.IsNullOrWhiteSpace(job.Id))
            job.Id = Path.GetFileName(packageDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(job.Executable))
            throw new InvalidDataException($"Job '{job.Id}' does not define Executable.");
        if (Path.IsPathRooted(job.Executable))
            throw new InvalidDataException($"Job '{job.Id}' Executable must be inside the job package, not an absolute path.");
        if (job.TimeoutSeconds < 0)
            throw new InvalidDataException($"Job '{job.Id}' TimeoutSeconds cannot be negative.");

        var executable = ResolveInsidePackage(packageDirectory, job.Executable);
        if (!File.Exists(executable))
            throw new InvalidDataException($"Job '{job.Id}' executable is missing from the package: {job.Executable}");

        if (!string.IsNullOrWhiteSpace(job.WorkingDirectory))
            _ = ResolveInsidePackage(packageDirectory, job.WorkingDirectory);

        foreach (var step in job.Plan)
            step.Validate(job.Id);

        return job;
    }

    public static string ResolveInsidePackage(string packageDirectory, string relativePath)
    {
        var root = Path.GetFullPath(packageDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, comparison) &&
            !string.Equals(full, root, comparison))
            throw new InvalidDataException($"Path escapes the job package: {relativePath}");

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
        if (DelayMs < 0)
            throw new InvalidDataException($"Job '{jobId}' contains a negative DelayMs.");

        switch (Type.Trim().ToLowerInvariant())
        {
            case "wait":
                if (DelayMs <= 0)
                    throw new InvalidDataException($"Job '{jobId}' wait steps require DelayMs > 0.");
                break;
            case "button":
                if (string.IsNullOrWhiteSpace(Button))
                    throw new InvalidDataException($"Job '{jobId}' button steps require Button.");
                if (DurationMs <= 0)
                    throw new InvalidDataException($"Job '{jobId}' button steps require DurationMs > 0.");
                break;
            case "screenshot":
                break;
            default:
                throw new InvalidDataException($"Job '{jobId}' contains unsupported plan step type '{Type}'.");
        }
    }
}
