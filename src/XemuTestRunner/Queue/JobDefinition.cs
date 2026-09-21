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

    public static JobDefinition Load(string path)
    {
        var job = JsonSerializer.Deserialize<JobDefinition>(File.ReadAllText(path), ConfigLoader.JsonOptions)
            ?? throw new InvalidDataException($"Job was empty or invalid: {path}");

        if (string.IsNullOrWhiteSpace(job.Id))
            job.Id = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(job.Executable))
            throw new InvalidDataException($"Job '{job.Id}' does not define Executable.");
        if (job.TimeoutSeconds < 0)
            throw new InvalidDataException($"Job '{job.Id}' TimeoutSeconds cannot be negative.");

        return job;
    }
}
