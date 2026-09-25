using System.Collections;
using System.Text.Json;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Runtime;

/// <summary>One requested process's storage preparation and post-exit ledger. Never starts a process or deletes a host cache.</summary>
public sealed class RunStorageSession
{
    public static IReadOnlyList<string> CacheEntries { get; } = Array.AsReadOnly(new[] { "cache", "shaders", "shader_cache_list" });
    private readonly RunIsolationDefinition? _policy;
    private readonly string _evidenceDirectory;
    private string? _cacheBase;
    private int _completed;
    public RunStorageReport Report { get; } = new();
    public IReadOnlyList<string> Arguments { get; private set; } = [];
    public IReadOnlyDictionary<string, string> Environment { get; private set; } = new Dictionary<string, string>();
    private RunStorageSession(RunIsolationDefinition? policy, string resultDirectory)
    {
        _policy = policy;
        _evidenceDirectory = Path.Combine(resultDirectory, "diagnostics", "run-state");
    }

    public static async Task<RunStorageSession> PrepareAsync(JobDefinition job, string executable, string workingDirectory,
        IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, string resultDirectory, CancellationToken ct)
    {
        var session = new RunStorageSession(job.RuntimeState.Isolation, resultDirectory) { Arguments = arguments };
        var effectiveEnvironment = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (DictionaryEntry value in System.Environment.GetEnvironmentVariables())
            if (value.Key is string key && value.Value is string text) effectiveEnvironment[key] = text;
        foreach (var item in environment) effectiveEnvironment[item.Key] = item.Value;
        session.Environment = effectiveEnvironment;
        if (session._policy is null)
        {
            session.Report.Status = "unmanaged";
            session.Report.Uncontrolled.Add("application-cache"); session.Report.Uncontrolled.Add("guest-state");
            session.SaveBestEffort(); return session;
        }
        var policy = session._policy;
        policy.Validate();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(policy.DeadlineSeconds));
        try
        {
            RunStateInventory.NoLinks(session._evidenceDirectory); Directory.CreateDirectory(session._evidenceDirectory);
            session.Report.CacheMode = policy.CacheMode; session.Report.CacheShaders = policy.CacheShaders;
            session.Report.DriverCache = policy.DriverCache; session.Report.AllowUncontrolledDriverCache = policy.AllowUncontrolledDriverCache;
            if (job.LaunchMode != "direct") throw new InvalidDataException("Managed state currently requires direct launch; RenderDoc routing is not qualified for this profile.");
            var config = await RunStateConfiguration.PrepareAsync(job, executable, workingDirectory, arguments,
                resultDirectory, session._evidenceDirectory, session.Report, deadline.Token).ConfigureAwait(false);
            session.Arguments = config.Arguments;
            ConfigureDriver(policy, session.Report, effectiveEnvironment, resultDirectory);
            var basePath = Path.GetDirectoryName(executable)!; session._cacheBase = basePath;
            var cache = Path.Combine(basePath, "cache"); session.Report.CacheDirectory = cache;
            session.Report.StoragePaths["applicationBase"] = basePath;
            session.Report.StoragePaths["openglShaders"] = Path.Combine(basePath, "shaders");
            session.Report.StoragePaths["openglReloadList"] = Path.Combine(basePath, "shader_cache_list");
            foreach (var entry in CacheEntries) RunStateInventory.NoLinks(Path.Combine(basePath, entry));
            var stateRoot = Path.Combine(resultDirectory, "state"); RunStateInventory.NoLinks(stateRoot); Directory.CreateDirectory(stateRoot);
            RunStateSnapshot? seed = null; string? seedDirectory = null;
            if (policy.CacheMode == "seeded")
            {
                seedDirectory = JobDefinition.ResolveInsidePackage(job.PackageDirectory ?? workingDirectory, policy.SeedDirectory!);
                foreach (var entry in CacheEntries)
                {
                    var live = Path.Combine(basePath, entry);
                    if (Within(Path.GetRelativePath(seedDirectory, live)) || Within(Path.GetRelativePath(live, seedDirectory)))
                        throw new InvalidDataException("Cache and immutable seed paths must not overlap.");
                }
                seed = await RunStateInventory.CaptureAsync(seedDirectory, policy.MaximumFiles, policy.MaximumBytes, deadline.Token).ConfigureAwait(false);
                if (!seed.Complete || !seed.TreeSha256.Equals(policy.SeedSha256, StringComparison.OrdinalIgnoreCase) || seed.Files.Any(file => !IsCachePath(file.Path)))
                    throw new InvalidDataException("Cache seed must be a complete pinned tree containing only cache/, shaders/ and shader_cache_list.");
                session.Report.SeedSha256 = seed.TreeSha256;
            }
            if (policy.CacheMode != "inherited")
            {
                var previous = Path.Combine(stateRoot, "prior-cache");
                if (Directory.Exists(previous)) throw new InvalidDataException("Run-specific prior-cache destination already exists.");
                foreach (var entry in CacheEntries)
                {
                    var live = Path.Combine(basePath, entry);
                    if (!Directory.Exists(live) && !File.Exists(live)) continue;
                    Directory.CreateDirectory(previous);
                    if (Directory.Exists(live)) Directory.Move(live, Path.Combine(previous, entry)); else File.Move(live, Path.Combine(previous, entry));
                    session.Report.PriorCacheDirectory = previous;
                }
            }
            Directory.CreateDirectory(cache); Directory.CreateDirectory(Path.Combine(basePath, "shaders"));
            if (seed is not null)
                foreach (var entry in seed.Files)
                    await RunStateInventory.CopyAsync(Path.Combine(seedDirectory!, entry.Path), RuntimeStateManager.ResolveInside(basePath, entry.Path), entry.Bytes, entry.Sha256, deadline.Token).ConfigureAwait(false);
            session.Report.Before = await RunStateInventory.CaptureAsync(basePath, policy.MaximumFiles, policy.MaximumBytes, deadline.Token, CacheEntries).ConfigureAwait(false);
            if (!session.Report.Before.Complete || (seed is not null && session.Report.Before.TreeSha256 != seed.TreeSha256))
                throw new InvalidDataException("Initial cache inventory is incomplete or differs from the pinned seed.");
            session.Report.ContractSha256 = RunStateInventory.Hash(JsonSerializer.SerializeToUtf8Bytes(new {
                schema = 1, cacheLayout = "xemu-gl-vk-v1", policy.CacheMode, policy.CacheShaders, policy.DriverCache, policy.AllowUncontrolledDriverCache,
                policy.RequirePrivateGuestState, config.SemanticHash, initialCache = session.Report.Before.TreeSha256,
                seeds = session.Report.RuntimeSeeds.Select(file => new { file.Source, file.Destination, file.Sha256 }).OrderBy(file => file.Destination).ToArray()
            }));
            session.Report.Status = "prepared"; session.Save(); return session;
        }
        catch (Exception error)
        {
            session.Report.Status = "preparationFailed"; session.Report.ComparisonReady = false;
            session.Report.Issues.Add(error is OperationCanceledException ? "preparation_deadline" : "preparation_failed");
            session.SaveBestEffort(); throw;
        }
    }

    public async Task CompleteAsync(bool targetStopped)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;
        Report.TargetStopped = targetStopped;
        if (_policy is null) { SaveBestEffort(); return; }
        if (!targetStopped)
        {
            Report.Status = "targetNotStopped"; Report.ComparisonReady = false; Report.Issues.Add("live_state_not_scanned");
            SaveBestEffort(); return;
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(_policy.DeadlineSeconds));
        try
        {
            // Shared inputs are verified outside execution/sampling, just like
            // private seed and final cache evidence. They are never recopied.
            await SharedReadOnlyInputs.VerifyAfterAsync(Report, deadline.Token).ConfigureAwait(false);
            if (Report.EffectiveConfigPath is not null)
            {
                var bytes = await RunStateConfiguration.ReadSmallAsync(Report.EffectiveConfigPath, deadline.Token).ConfigureAwait(false);
                Report.ConfigAfterSha256 = RunStateInventory.Hash(bytes);
                await File.WriteAllBytesAsync(Path.Combine(_evidenceDirectory, "config-after.toml"), bytes, deadline.Token).ConfigureAwait(false);
            }
            if (_cacheBase is not null) Report.After = await RunStateInventory.CaptureAsync(_cacheBase, _policy.MaximumFiles, _policy.MaximumBytes, deadline.Token, CacheEntries).ConfigureAwait(false);
            if (Report.DriverCacheDirectory is not null) Report.DriverAfter = await RunStateInventory.CaptureAsync(Report.DriverCacheDirectory, _policy.MaximumFiles, _policy.MaximumBytes, deadline.Token).ConfigureAwait(false);
            Report.Status = Report.After?.Complete == true && (Report.DriverAfter is null || Report.DriverAfter.Complete) ? "complete" : "incomplete";
            Report.ComparisonReady = Report.Status == "complete" && Report.Before?.Complete == true && Report.CacheMode != "inherited" &&
                Report.AllowUncontrolledDriverCache && !Report.Uncontrolled.Any(value => value.StartsWith("guest-", StringComparison.Ordinal));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException or JsonException)
        {
            Report.Status = "incomplete"; Report.ComparisonReady = false;
            Report.Issues.Add(error is OperationCanceledException ? "final_state_deadline" : "final_state_unavailable");
        }
        SaveBestEffort();
    }

    private static bool IsCachePath(string relative) => relative == "shader_cache_list" || relative.StartsWith("cache/", StringComparison.Ordinal) || relative.StartsWith("shaders/", StringComparison.Ordinal);
    private static bool Within(string relative) => !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    private static void ConfigureDriver(RunIsolationDefinition policy, RunStorageReport report, Dictionary<string, string> environment, string result)
    {
        bool IsCacheKey(string key) => key.StartsWith("MESA_SHADER_CACHE", StringComparison.Ordinal) || key.StartsWith("MESA_DISK_CACHE", StringComparison.Ordinal) || key.StartsWith("__GL_SHADER_DISK_CACHE", StringComparison.Ordinal);
        foreach (var key in environment.Keys.Where(IsCacheKey).OrderBy(key => key).Take(32).ToArray()) report.CacheEnvironment["inherited:" + key] = environment[key].Length <= 512 ? environment[key] : "<overlength>";
        if (policy.DriverCache == "uncontrolled") return;
        if (!OperatingSystem.IsLinux()) throw new InvalidDataException("Driver cache adapters currently support Linux only; Windows must remain explicitly uncontrolled.");
        var directory = Path.Combine(result, "state", "driver-cache"); RunStateInventory.NoLinks(directory);
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) throw new InvalidDataException("The per-run driver directory is not empty.");
        Directory.CreateDirectory(directory);
        foreach (var key in environment.Keys.Where(IsCacheKey).ToArray()) environment.Remove(key);
        if (policy.DriverCache == "mesa")
        {
            environment["MESA_SHADER_CACHE_DIR"] = directory; environment["MESA_SHADER_CACHE_DISABLE"] = "false"; environment["MESA_SHADER_CACHE_MAX_SIZE"] = "256M";
        }
        else { environment["__GL_SHADER_DISK_CACHE_PATH"] = directory; environment["__GL_SHADER_DISK_CACHE"] = "1"; }
        report.DriverCacheDirectory = directory;
        foreach (var key in environment.Keys.Where(IsCacheKey)) report.CacheEnvironment["effective:" + key] = environment[key];
        report.DriverNamespaceVerified = false;
    }
    private void Save() { RunStateInventory.NoLinks(_evidenceDirectory); AtomicJson.Write(Path.Combine(_evidenceDirectory, "report.json"), Report); }
    private void SaveBestEffort()
    {
        try { Save(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { Report.ComparisonReady = false; System.Diagnostics.Trace.TraceError("Run state report publication failed: " + error.Message); }
    }
}
