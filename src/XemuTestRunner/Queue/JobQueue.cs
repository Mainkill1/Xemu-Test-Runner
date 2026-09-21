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
            return new QueueSnapshot(CountPackages(_paths.Pending), CountPackages(_paths.Testing), CountPackages(_paths.Tested));
    }

    public IReadOnlyList<string> GetTestingJobs()
    {
        lock (_gate)
            return EnumeratePackages(_paths.Testing).OrderBy(Path.GetFileName).ToArray();
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

        foreach (var package in interrupted)
        {
            var name = Path.GetFileName(package);
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff");
            var record = new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                action = "retry",
                jobPackage = name,
                reason = "Job package was present in Testing when the runner started. The prior run did not complete cleanly."
            };

            File.WriteAllText(
                Path.Combine(recoveryDirectory, $"{stamp}-{name}.json"),
                JsonSerializer.Serialize(record, ConfigLoader.JsonOptions));

            Directory.Move(package, UniqueDirectory(_paths.Pending, name));
        }
    }

    public string? TryClaimNext()
    {
        lock (_gate)
        {
            if (EnumeratePackages(_paths.Testing).Any())
                return null;

            var pending = EnumeratePackages(_paths.Pending)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (pending is null)
                return null;

            var destination = Path.Combine(_paths.Testing, Path.GetFileName(pending));
            Directory.Move(pending, destination);
            return destination;
        }
    }

    public string Complete(string testingPackage)
    {
        lock (_gate)
        {
            var destination = UniqueDirectory(_paths.Tested, Path.GetFileName(testingPackage));
            Directory.Move(testingPackage, destination);
            return destination;
        }
    }

    private static IEnumerable<string> EnumeratePackages(string root)
    {
        if (!Directory.Exists(root))
            yield break;

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (Path.GetFileName(directory).StartsWith('.', StringComparison.Ordinal))
                continue;
            if (File.Exists(Path.Combine(directory, "job.json")))
                yield return directory;
        }
    }

    private static int CountPackages(string root) => EnumeratePackages(root).Count();

    private static string UniqueDirectory(string parent, string name)
    {
        var path = Path.Combine(parent, name);
        if (!Directory.Exists(path))
            return path;

        return Path.Combine(parent, $"{name}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}");
    }
}

public sealed record QueueSnapshot(int Pending, int Testing, int Tested);
