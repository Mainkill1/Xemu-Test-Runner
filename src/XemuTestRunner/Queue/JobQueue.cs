using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Queue;

public sealed class JobQueue
{
    private readonly RunnerConfig _config;
    private readonly RunnerPaths _paths;
    private readonly object _gate = new();
    private readonly Dictionary<string, PackageObservation> _observations =
        new(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

    public JobQueue(RunnerConfig config, RunnerPaths paths)
    {
        _config = config;
        _paths = paths;
    }

    public void EnsureDirectories()
    {
        foreach (var path in new[]
        {
            _paths.Workspace,
            _paths.Pending,
            _paths.Testing,
            _paths.Tested,
            _paths.Results,
            _paths.FileRoot
        })
            Directory.CreateDirectory(path);
    }

    public QueueSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new(
                VisibleDirectories(_paths.Pending).Count(),
                VisibleDirectories(_paths.Testing).Count(),
                VisibleDirectories(_paths.Tested).Count());
        }
    }

    public IReadOnlyList<string> GetTestingJobs()
    {
        lock (_gate)
        {
            return VisibleDirectories(_paths.Testing)
                .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public void RecoverInterrupted()
    {
        // Called only after WorkspaceLease has been acquired by the engine.
        foreach (var package in GetTestingJobs())
        {
            try
            {
                var attempt = AttemptJournal.Read(package);
                var final = attempt is not null &&
                    AttemptJournal.HasFinalResult(_paths.Results, attempt);
                var action = AttemptJournal.RecoveryDecision(
                    attempt,
                    final,
                    _config.Reliability.MaxInterruptedRetries);

                if (!final &&
                    _config.Queue.InterruptedAction.Equals(
                        "hold",
                        StringComparison.OrdinalIgnoreCase))
                    action = RecoveryAction.Hold;

                AtomicJson.Write(
                    Path.Combine(package, ".runner-recovery.json"),
                    new
                    {
                        atUtc = DateTimeOffset.UtcNow,
                        action = action.ToString(),
                        reason = action == RecoveryAction.Hold
                            ? "Manual inspection required: live, ambiguous, uninspectable, or explicitly held attempt."
                            : null
                    });

                if (action == RecoveryAction.Hold)
                    continue;

                if (action == RecoveryAction.Archive)
                {
                    Complete(package);
                    continue;
                }

                if (attempt is null)
                    continue;

                AtomicJson.Write(
                    Path.Combine(_paths.Results, attempt.RunId, "result.json"),
                    new
                    {
                        runId = attempt.RunId,
                        jobPackage = Path.GetFileName(package),
                        status = "interrupted",
                        attempt = attempt.Attempt,
                        endedUtc = DateTimeOffset.UtcNow,
                        retryScheduled = action == RecoveryAction.Retry,
                        retriesExhausted = action == RecoveryAction.Exhausted
                    });

                if (action == RecoveryAction.Exhausted)
                    Complete(package);
                else
                    Directory.Move(
                        package,
                        UniqueDirectory(
                            _paths.Pending,
                            Path.GetFileName(package)));
            }
            catch (Exception ex) when (
                ex is JsonException or
                InvalidDataException or
                UnauthorizedAccessException or
                IOException)
            {
                try
                {
                    AtomicJson.Write(
                        Path.Combine(package, ".runner-recovery.json"),
                        new
                        {
                            action = "Hold",
                            error = ex.Message
                        });
                }
                catch
                {
                    // Preserve the package in Testing even when we cannot write
                    // the recovery note.
                }
            }
        }
    }

    public QueueClaimResult TryClaimNext()
    {
        lock (_gate)
        {
            if (VisibleDirectories(_paths.Testing).Any())
                return QueueClaimResult.None;

            var candidates = VisibleDirectories(_paths.Pending)
                .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                .ToArray();

            if (candidates.Length == 0)
            {
                _observations.Clear();
                return QueueClaimResult.None;
            }

            var candidate = candidates[0];
            var probe = ProbePackage(candidate);

            if (!probe.Ready)
            {
                _observations.Remove(candidate);
                return new QueueClaimResult(
                    null,
                    probe.Issue);
            }

            var now = DateTimeOffset.UtcNow;
            if (!_observations.TryGetValue(candidate, out var observation) ||
                !string.Equals(
                    observation.Signature,
                    probe.Signature,
                    StringComparison.Ordinal))
            {
                _observations[candidate] = new PackageObservation(
                    probe.Signature!,
                    now);

                return new QueueClaimResult(
                    null,
                    Issue(
                        "package_stabilizing",
                        candidate,
                        $"Package '{Path.GetFileName(candidate)}' is visible but has not remained unchanged for {_config.Queue.PackageStabilityMs} ms. " +
                        "The runner is waiting for file copy/move activity to finish.",
                        retryable: true,
                        holdsTesting: false));
            }

            if ((now - observation.FirstStableUtc).TotalMilliseconds <
                _config.Queue.PackageStabilityMs)
            {
                return new QueueClaimResult(
                    null,
                    Issue(
                        "package_stabilizing",
                        candidate,
                        $"Package '{Path.GetFileName(candidate)}' is still inside the {_config.Queue.PackageStabilityMs} ms stability window.",
                        retryable: true,
                        holdsTesting: false));
            }

            var destination = Path.Combine(
                _paths.Testing,
                Path.GetFileName(candidate));

            try
            {
                if (Directory.Exists(destination))
                {
                    return new QueueClaimResult(
                        null,
                        Issue(
                            "package_claim_conflict",
                            candidate,
                            $"Cannot claim '{Path.GetFileName(candidate)}' because a directory with the same name already exists in Testing.",
                            retryable: false,
                            holdsTesting: true));
                }

                Directory.Move(candidate, destination);
            }
            catch (DirectoryNotFoundException ex)
            {
                _observations.Remove(candidate);
                return new QueueClaimResult(
                    null,
                    Issue(
                        "package_moved",
                        candidate,
                        $"Package '{Path.GetFileName(candidate)}' disappeared or was moved while the runner was claiming it: {ex.Message}",
                        retryable: true,
                        holdsTesting: false));
            }
            catch (UnauthorizedAccessException ex)
            {
                return new QueueClaimResult(
                    null,
                    Issue(
                        "package_access_denied",
                        candidate,
                        $"Package '{Path.GetFileName(candidate)}' could not be moved into Testing because access was denied: {ex.Message}",
                        retryable: false,
                        holdsTesting: false));
            }
            catch (IOException ex)
            {
                return new QueueClaimResult(
                    null,
                    Issue(
                        "package_busy",
                        candidate,
                        $"Package '{Path.GetFileName(candidate)}' could not be moved into Testing. Another process may still be copying, renaming, scanning, or holding files in it: {ex.Message}",
                        retryable: true,
                        holdsTesting: false));
            }

            _observations.Remove(candidate);

            var afterMove = ProbePackage(destination);
            if (!afterMove.Ready ||
                !string.Equals(
                    probe.Signature,
                    afterMove.Signature,
                    StringComparison.Ordinal))
            {
                var issue = afterMove.Ready
                    ? Issue(
                        "package_changed_during_claim",
                        destination,
                        $"Package '{Path.GetFileName(destination)}' changed while it was being claimed. It has been left in Testing and will not be launched.",
                        retryable: false,
                        holdsTesting: true)
                    : afterMove.Issue! with { HoldsTesting = true };

                TryWriteClaimIssue(destination, issue);
                return new QueueClaimResult(null, issue);
            }

            AtomicJson.Write(
                Path.Combine(destination, ".runner-claim.json"),
                new
                {
                    claimedUtc = DateTimeOffset.UtcNow,
                    source = candidate,
                    signature = afterMove.Signature
                });

            return new QueueClaimResult(destination, null);
        }
    }

    public QueueIssue? VerifyClaimedPackage(string package)
    {
        lock (_gate)
        {
            var claimPath = Path.Combine(package, ".runner-claim.json");
            if (!File.Exists(claimPath))
            {
                var issue = Issue(
                    "package_claim_missing",
                    package,
                    $"Package '{Path.GetFileName(package)}' is in Testing but has no runner claim record. It will not be launched automatically.",
                    retryable: false,
                    holdsTesting: true);
                TryWriteClaimIssue(package, issue);
                return issue;
            }

            string? expectedSignature;
            try
            {
                using var document = JsonDocument.Parse(
                    File.ReadAllText(claimPath));
                expectedSignature = document.RootElement
                    .GetProperty("signature")
                    .GetString();
            }
            catch (Exception ex) when (
                ex is IOException or
                JsonException or
                InvalidOperationException or
                KeyNotFoundException)
            {
                var issue = Issue(
                    "package_claim_invalid",
                    package,
                    $"Package '{Path.GetFileName(package)}' has an unreadable claim record: {ex.Message}",
                    retryable: false,
                    holdsTesting: true);
                TryWriteClaimIssue(package, issue);
                return issue;
            }

            var probe = ProbePackage(package);
            if (!probe.Ready)
            {
                var issue = probe.Issue! with { HoldsTesting = true };
                TryWriteClaimIssue(package, issue);
                return issue;
            }

            if (!string.Equals(
                    expectedSignature,
                    probe.Signature,
                    StringComparison.Ordinal))
            {
                var issue = Issue(
                    "package_changed_during_preflight",
                    package,
                    $"Package '{Path.GetFileName(package)}' changed after it was claimed. It has been left in Testing and will not be launched.",
                    retryable: false,
                    holdsTesting: true);
                TryWriteClaimIssue(package, issue);
                return issue;
            }

            return null;
        }
    }

    public string Complete(string package)
    {
        lock (_gate)
        {
            var destination = UniqueDirectory(
                _paths.Tested,
                Path.GetFileName(package));
            Directory.Move(package, destination);
            return destination;
        }
    }

    private PackageProbe ProbePackage(string package)
    {
        if (!Directory.Exists(package))
        {
            return PackageProbe.Failed(
                Issue(
                    "package_moved",
                    package,
                    $"Package '{Path.GetFileName(package)}' no longer exists.",
                    retryable: true,
                    holdsTesting: false));
        }

        var manifest = Path.Combine(package, "job.json");
        if (!File.Exists(manifest))
        {
            return PackageProbe.Failed(
                Issue(
                    "package_incomplete",
                    package,
                    $"Package '{Path.GetFileName(package)}' is visible but job.json is missing. " +
                    "Finish copying the package or stage it under a dot-prefixed directory until complete.",
                    retryable: true,
                    holdsTesting: false));
        }

        JobDefinition job;
        try
        {
            job = JobDefinition.LoadPackage(package);
        }
        catch (JsonException ex)
        {
            return PackageProbe.Failed(
                Issue(
                    "package_incomplete",
                    package,
                    $"Package '{Path.GetFileName(package)}' has a job.json that cannot be parsed yet. It may still be being written: {ex.Message}",
                    retryable: true,
                    holdsTesting: false));
        }
        catch (IOException ex)
        {
            return PackageProbe.Failed(
                Issue(
                    "package_busy",
                    package,
                    $"Package '{Path.GetFileName(package)}' could not be read. Another process may still have files open: {ex.Message}",
                    retryable: true,
                    holdsTesting: false));
        }
        catch (UnauthorizedAccessException ex)
        {
            return PackageProbe.Failed(
                Issue(
                    "package_access_denied",
                    package,
                    $"Package '{Path.GetFileName(package)}' cannot be read: {ex.Message}",
                    retryable: false,
                    holdsTesting: false));
        }
        catch (InvalidDataException ex)
        {
            return PackageProbe.Failed(
                Issue(
                    "package_invalid",
                    package,
                    $"Package '{Path.GetFileName(package)}' has an invalid job plan: {ex.Message}",
                    retryable: false,
                    holdsTesting: false));
        }

        var workingDirectory = JobDefinition.ResolveInsidePackage(
            package,
            job.WorkingDirectory ?? ".");
        if (!Directory.Exists(workingDirectory))
        {
            return PackageProbe.Failed(
                Issue(
                    "package_incomplete",
                    package,
                    $"Package '{Path.GetFileName(package)}' is missing working directory '{job.WorkingDirectory ?? "."}'.",
                    retryable: true,
                    holdsTesting: false));
        }

        var critical = new List<string>
        {
            manifest,
            JobDefinition.ResolveInsidePackage(package, job.Executable)
        };
        critical.AddRange(
            job.RequiredFiles.Select(relative =>
                JobDefinition.ResolveInsidePackage(package, relative)));

        var stamps = new List<string>(critical.Count);
        foreach (var path in critical.Distinct(
                     OperatingSystem.IsWindows()
                         ? StringComparer.OrdinalIgnoreCase
                         : StringComparer.Ordinal))
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    return PackageProbe.Failed(
                        Issue(
                            "package_incomplete",
                            package,
                            $"Package '{Path.GetFileName(package)}' is missing required file '{Path.GetRelativePath(package, path)}'.",
                            retryable: true,
                            holdsTesting: false));
                }

                // Opening with read sharing catches common copy/update tools that
                // deliberately hold an exclusive handle while replacing a file.
                using (var stream = new FileStream(
                           path,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.Read))
                {
                    _ = stream.Length;
                }

                info.Refresh();
                stamps.Add(
                    $"{Path.GetRelativePath(package, path).Replace(Path.DirectorySeparatorChar, '/')}|" +
                    $"{info.Length}|{info.LastWriteTimeUtc.Ticks}");
            }
            catch (IOException ex)
            {
                return PackageProbe.Failed(
                    Issue(
                        "package_busy",
                        package,
                        $"Package '{Path.GetFileName(package)}' is not ready because '{Path.GetRelativePath(package, path)}' is busy or being replaced: {ex.Message}",
                        retryable: true,
                        holdsTesting: false));
            }
            catch (UnauthorizedAccessException ex)
            {
                return PackageProbe.Failed(
                    Issue(
                        "package_access_denied",
                        package,
                        $"Package '{Path.GetFileName(package)}' cannot read '{Path.GetRelativePath(package, path)}': {ex.Message}",
                        retryable: false,
                        holdsTesting: false));
            }
        }

        stamps.Sort(StringComparer.Ordinal);
        return PackageProbe.Success(string.Join("\n", stamps));
    }

    private static QueueIssue Issue(
        string code,
        string package,
        string message,
        bool retryable,
        bool holdsTesting) =>
        new(
            code,
            Path.GetFileName(package),
            message,
            DateTimeOffset.UtcNow,
            retryable,
            holdsTesting);

    private static void TryWriteClaimIssue(
        string package,
        QueueIssue issue)
    {
        try
        {
            AtomicJson.Write(
                Path.Combine(package, ".runner-claim-error.json"),
                issue);
        }
        catch
        {
        }
    }

    private static IEnumerable<string> VisibleDirectories(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateDirectories(root)
                .Where(path =>
                    !Path.GetFileName(path)
                        .StartsWith(".", StringComparison.Ordinal))
            : [];

    private static string UniqueDirectory(
        string parent,
        string name)
    {
        var path = Path.Combine(parent, name);
        return Directory.Exists(path)
            ? Path.Combine(
                parent,
                name + "-" + Guid.NewGuid().ToString("N"))
            : path;
    }

    private sealed record PackageObservation(
        string Signature,
        DateTimeOffset FirstStableUtc);

    private sealed record PackageProbe(
        bool Ready,
        string? Signature,
        QueueIssue? Issue)
    {
        public static PackageProbe Success(string signature) =>
            new(true, signature, null);

        public static PackageProbe Failed(QueueIssue issue) =>
            new(false, null, issue);
    }
}

public sealed record QueueSnapshot(
    int Pending,
    int Testing,
    int Tested);

public sealed record QueueIssue(
    string Code,
    string Package,
    string Message,
    DateTimeOffset DetectedUtc,
    bool Retryable,
    bool HoldsTesting);

public sealed record QueueClaimResult(
    string? Package,
    QueueIssue? Issue)
{
    public static QueueClaimResult None { get; } =
        new(null, null);
}
