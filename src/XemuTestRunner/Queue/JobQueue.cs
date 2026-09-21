using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Queue;

public sealed class JobQueue
{
    private readonly RunnerConfig _config;
    private readonly RunnerPaths _paths;
    private readonly object _gate = new();
    public JobQueue(RunnerConfig config, RunnerPaths paths) { _config = config; _paths = paths; }
    public void EnsureDirectories()
    {
        foreach (var path in new[] { _paths.Workspace, _paths.Pending, _paths.Testing, _paths.Tested, _paths.Results, _paths.FileRoot })
            Directory.CreateDirectory(path);
    }
    public QueueSnapshot Snapshot()
    {
        lock (_gate) return new(Packages(_paths.Pending).Count(), VisibleDirectories(_paths.Testing).Count(), Packages(_paths.Tested).Count());
    }
    public IReadOnlyList<string> GetTestingJobs()
    {
        lock (_gate) return VisibleDirectories(_paths.Testing).OrderBy(Path.GetFileName).ToArray();
    }
    public void RecoverInterrupted()
    {
        // Called only after WorkspaceLease has been acquired by the engine.
        foreach (var package in GetTestingJobs())
        {
            try
            {
                var attempt = AttemptJournal.Read(package);
                var final = attempt is not null && AttemptJournal.HasFinalResult(_paths.Results, attempt);
                var action = AttemptJournal.RecoveryDecision(attempt, final, _config.Reliability.MaxInterruptedRetries);
                if (!final && _config.Queue.InterruptedAction.Equals("hold", StringComparison.OrdinalIgnoreCase)) action = RecoveryAction.Hold;
                AtomicJson.Write(Path.Combine(package, ".runner-recovery.json"), new { atUtc = DateTimeOffset.UtcNow, action = action.ToString(),
                    reason = action == RecoveryAction.Hold ? "Manual inspection required: live, ambiguous, uninspectable, or explicitly held attempt." : null });
                if (action == RecoveryAction.Hold) continue;
                if (action == RecoveryAction.Archive) { Complete(package); continue; }
                if (attempt is null) continue;
                // Preserve an outcome for the interrupted attempt before retrying it.
                AtomicJson.Write(Path.Combine(_paths.Results, attempt.RunId, "result.json"), new
                {
                    runId = attempt.RunId, jobPackage = Path.GetFileName(package), status = "interrupted",
                    attempt = attempt.Attempt, endedUtc = DateTimeOffset.UtcNow,
                    retryScheduled = action == RecoveryAction.Retry, retriesExhausted = action == RecoveryAction.Exhausted
                });
                if (action == RecoveryAction.Exhausted) Complete(package);
                else Directory.Move(package, UniqueDirectory(_paths.Pending, Path.GetFileName(package)));
            }
            catch (Exception e) when (e is JsonException or InvalidDataException or UnauthorizedAccessException)
            {
                // Malformed journals are evidence. Keep the package where it was.
                AtomicJson.Write(Path.Combine(package, ".runner-recovery.json"), new { action = "Hold", error = e.Message });
            }
        }
    }
    public string? TryClaimNext()
    {
        lock (_gate)
        {
            if (VisibleDirectories(_paths.Testing).Any()) return null;
            var next = Packages(_paths.Pending).OrderBy(Path.GetFileName, StringComparer.Ordinal).FirstOrDefault();
            if (next is null) return null;
            var destination = Path.Combine(_paths.Testing, Path.GetFileName(next));
            Directory.Move(next, destination);
            return destination;
        }
    }
    public string Complete(string package)
    {
        lock (_gate)
        {
            var destination = UniqueDirectory(_paths.Tested, Path.GetFileName(package));
            Directory.Move(package, destination);
            return destination;
        }
    }
    private static IEnumerable<string> VisibleDirectories(string root) => Directory.Exists(root)
        ? Directory.EnumerateDirectories(root).Where(p => !Path.GetFileName(p).StartsWith(".", StringComparison.Ordinal)) : [];
    private static IEnumerable<string> Packages(string root) => VisibleDirectories(root).Where(p => File.Exists(Path.Combine(p, "job.json")));
    private static string UniqueDirectory(string parent, string name)
    {
        var path = Path.Combine(parent, name);
        return Directory.Exists(path) ? Path.Combine(parent, name + "-" + Guid.NewGuid().ToString("N")) : path;
    }
}
public sealed record QueueSnapshot(int Pending, int Testing, int Tested);
