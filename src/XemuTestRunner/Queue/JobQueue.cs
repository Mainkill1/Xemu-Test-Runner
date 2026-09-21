using System.Text.Json;
using XemuTestRunner.Config;

namespace XemuTestRunner.Queue;

public sealed class JobQueue
{
    private readonly RunnerConfig _config;
    private readonly RunnerPaths _paths;
    private readonly object _gate = new();

    public JobQueue(RunnerConfig config, RunnerPaths paths)
    {
        _config = config;
        _paths = paths;
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(_paths.Workspace);
        Directory.CreateDirectory(_paths.Pending);
        Directory.CreateDirectory(_paths.Testing);
        Directory.CreateDirectory(_paths.Tested);
        Directory.CreateDirectory(_paths.Results);
        Directory.CreateDirectory(_paths.FileRoot);
    }

    public QueueSnapshot Snapshot()
    {
        lock (_gate)
            return new QueueSnapshot(CountJobs(_paths.Pending), CountJobs(_paths.Testing), CountJobs(_paths.Tested));
    }

    public IReadOnlyList<string> GetTestingJobs()
    {
        lock (_gate)
            return Directory.EnumerateFiles(_paths.Testing, "*.json").OrderBy(Path.GetFileName).ToArray();
    }

    public void RecoverInterrupted()
    {
        var interrupted = GetTestingJobs();
        if (interrupted.Count == 0)
            return;

        if (_config.Queue.InterruptedAction.Equals("hold", StringComparison.OrdinalIgnoreCase))
            return;

        var recoveryDirectory = Path.Combine(_paths.Results, "_recovery");
        Directory.CreateDirectory(recoveryDirectory);

        foreach (var path in interrupted)
        {
            var name = Path.GetFileName(path);
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff");
            var record = new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                action = "retry",
                job = name,
                reason = "Job was present in Testing when the runner started. The prior run did not complete cleanly."
            };
            File.WriteAllText(
                Path.Combine(recoveryDirectory, $"{stamp}-{Path.GetFileNameWithoutExtension(name)}.json"),
                JsonSerializer.Serialize(record, ConfigLoader.JsonOptions));

            File.Move(path, UniquePath(_paths.Pending, name));
        }
    }

    public string? TryClaimNext()
    {
        lock (_gate)
        {
            if (Directory.EnumerateFiles(_paths.Testing, "*.json").Any())
                return null;

            var pending = Directory.EnumerateFiles(_paths.Pending, "*.json")
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (pending is null)
                return null;

            var destination = Path.Combine(_paths.Testing, Path.GetFileName(pending));
            File.Move(pending, destination);
            return destination;
        }
    }

    public string Enqueue(JobDefinition job)
    {
        if (string.IsNullOrWhiteSpace(job.Id))
            job.Id = $"job-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}";
        if (string.IsNullOrWhiteSpace(job.Executable))
            throw new InvalidDataException("Job Executable is required.");

        var safeName = string.Concat(job.Id.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var temporary = Path.Combine(_paths.Pending, $".enqueue-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporary, JsonSerializer.Serialize(job, ConfigLoader.JsonOptions));

        try
        {
            lock (_gate)
            {
                var path = UniquePath(_paths.Pending, safeName + ".json");
                File.Move(temporary, path);
                return path;
            }
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }

    public string Complete(string testingPath)
    {
        lock (_gate)
        {
            var destination = UniquePath(_paths.Tested, Path.GetFileName(testingPath));
            File.Move(testingPath, destination);
            return destination;
        }
    }

    private static int CountJobs(string path) => Directory.Exists(path)
        ? Directory.EnumerateFiles(path, "*.json").Count()
        : 0;

    private static string UniquePath(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        if (!File.Exists(path))
            return path;

        return Path.Combine(
            directory,
            $"{Path.GetFileNameWithoutExtension(name)}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}{Path.GetExtension(name)}");
    }
}

public sealed record QueueSnapshot(int Pending, int Testing, int Tested);
