using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Runtime;

if (args.Contains("--fake-target", StringComparer.Ordinal))
{
    await Task.Delay(100);
    return 0;
}

var failures = 0;
async Task Check(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
}
void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
string Digest(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

var root = Path.Combine(Path.GetTempPath(), "disk-asset-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await Check("catalog xiso seed is private per run and deleted after evidence while the shared asset survives", async () =>
    {
        var fixture = Path.Combine(root, "xiso");
        var seed = Encoding.UTF8.GetBytes("tiny-xiso-seed");
        var sha = Digest(seed);
        var (config, paths) = await SetupAsync(fixture);
        WriteCatalogAsset(paths.Workspace, "xiso-empty-v1", "xiso-seed", seed, sha);
        var package = CreatePackage(paths.Pending, "xiso-run", new
        {
            id = "xiso-run",
            targetOs = OperatingSystem.IsWindows() ? "windows" : "linux",
            executable = Path.GetFileName(Environment.ProcessPath!),
            arguments = new[] { "--fake-target" },
            timeoutSeconds = 3,
            runtimeState = new
            {
                enabled = true,
                diskAssets = new[] { new { assetId = "xiso-empty-v1", expectedSha256 = sha, destination = "xbox_hdd.qcow2" } }
            }
        });
        CopySelf(package);
        var engine = new RunnerEngine(config, paths);
        await engine.RunAsync(once: true, CancellationToken.None);

        var result = Directory.GetDirectories(paths.Results).Single();
        using var runtime = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result, "runtime-state.json")));
        var files = runtime.RootElement.GetProperty("files");
        Require(files.GetArrayLength() == 1, "Catalog disk was not materialized.");
        var file = files[0];
        Require(file.GetProperty("DiskAssetId").GetString() == "xiso-empty-v1", "Asset provenance missing.");
        Require(file.GetProperty("Sha256").GetString() == sha, "Runtime disk hash missing.");
        var runtimeDirectory = runtime.RootElement.GetProperty("directory").GetString()!;
        Require(!File.Exists(Path.Combine(runtimeDirectory, "xbox_hdd.qcow2")), "Transient XISO HDD survived finalized evidence.");
        using var cleanup = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result, "runtime-cleanup.json")));
        Require(cleanup.RootElement.GetProperty("state").GetString() == "complete", "Cleanup receipt not finalized.");
        Require(File.Exists(Path.Combine(paths.Workspace, "DiskAssets", "xiso-empty-v1", "disk.qcow2")), "Shared catalog asset was deleted.");
        Require(!File.Exists(Path.Combine(Directory.GetDirectories(paths.Tested).Single(), "xbox_hdd.qcow2")), "Runtime HDD leaked into Tested package.");
    });

    await Check("snapshot carrier is copied byte-for-byte and existing snapshot name is force-loaded", async () =>
    {
        var fixture = Path.Combine(root, "snapshot");
        var carrier = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
        var sha = Digest(carrier);
        var (config, paths) = await SetupAsync(fixture);
        WriteCatalogAsset(paths.Workspace, "snapshot-main", "snapshot-carrier", carrier, sha);
        var package = CreatePackage(paths.Pending, "snapshot-run", new
        {
            id = "snapshot-run",
            targetOs = OperatingSystem.IsWindows() ? "windows" : "linux",
            executable = Path.GetFileName(Environment.ProcessPath!),
            arguments = new[] { "--fake-target" },
            timeoutSeconds = 3,
            snapshotName = "gameplay-a",
            runtimeState = new
            {
                enabled = true,
                diskAssets = new[] { new { assetId = "snapshot-main", expectedSha256 = sha, destination = "xbox_hdd.qcow2", retention = "keep" } }
            }
        });
        CopySelf(package);
        var engine = new RunnerEngine(config, paths);
        await engine.RunAsync(once: true, CancellationToken.None);

        var result = Directory.GetDirectories(paths.Results).Single();
        using var runtime = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result, "runtime-state.json")));
        var runtimeDirectory = runtime.RootElement.GetProperty("directory").GetString()!;
        var copied = await File.ReadAllBytesAsync(Path.Combine(runtimeDirectory, "xbox_hdd.qcow2"));
        Require(copied.SequenceEqual(carrier), "Snapshot carrier was transformed instead of byte-copied.");
        using var launch = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result, "launch.json")));
        var launchArgs = launch.RootElement.GetProperty("arguments").EnumerateArray().Select(x => x.GetString()).ToArray();
        var index = Array.IndexOf(launchArgs, "-loadvm");
        Require(index >= 0 && index + 1 < launchArgs.Length && launchArgs[index + 1] == "gameplay-a", "SnapshotName was not passed as -loadvm.");
    });

    await Check("catalog hash mismatch blocks target launch", async () =>
    {
        var fixture = Path.Combine(root, "mismatch");
        var expected = Encoding.UTF8.GetBytes("expected");
        var actual = Encoding.UTF8.GetBytes("tampered");
        var sha = Digest(expected);
        var (config, paths) = await SetupAsync(fixture);
        WriteCatalogAsset(paths.Workspace, "bad-seed", "xiso-seed", actual, sha);
        var package = CreatePackage(paths.Pending, "bad-run", new
        {
            id = "bad-run",
            targetOs = OperatingSystem.IsWindows() ? "windows" : "linux",
            executable = Path.GetFileName(Environment.ProcessPath!),
            arguments = new[] { "--fake-target" },
            timeoutSeconds = 3,
            runtimeState = new
            {
                enabled = true,
                diskAssets = new[] { new { assetId = "bad-seed", expectedSha256 = sha, destination = "xbox_hdd.qcow2" } }
            }
        });
        CopySelf(package);
        var engine = new RunnerEngine(config, paths);
        await engine.RunAsync(once: true, CancellationToken.None);
        var result = Directory.GetDirectories(paths.Results).Single();
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result, "result.json")));
        Require(document.RootElement.GetProperty("processId").ValueKind == JsonValueKind.Null, "Target launched with a corrupted disk asset.");
        Require((document.RootElement.GetProperty("detail").GetString() ?? "").Contains("SHA-256", StringComparison.OrdinalIgnoreCase), "Hash failure is not visible.");
    });

    await Check("startup refuses a cleanup receipt whose run ID does not match its result directory", async () =>
    {
        var fixture = Path.Combine(root, "forged-recovery");
        var (config, paths) = await SetupAsync(fixture);
        var otherRuntime = Path.Combine(paths.Workspace, "Runtime", "other-run");
        Directory.CreateDirectory(otherRuntime);
        var disk = Path.Combine(otherRuntime, "xbox_hdd.qcow2");
        await File.WriteAllTextAsync(disk, "must-survive");
        var forgedResult = Path.Combine(paths.Results, "forged-run");
        Directory.CreateDirectory(forgedResult);
        await File.WriteAllTextAsync(Path.Combine(forgedResult, "runtime-cleanup.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            runId = "other-run",
            state = "pending",
            targetStopped = true,
            evidenceFinalized = true,
            runtimeDirectory = otherRuntime,
            files = new[] { new { assetId = "xiso-empty-v1", path = "xbox_hdd.qcow2", retention = "deleteAfterEvidence", deleted = false } }
        }, ConfigLoader.JsonOptions));
        var engine = new RunnerEngine(config, paths);
        await engine.RunAsync(once: true, CancellationToken.None);
        Require(File.Exists(disk), "A receipt in one result directory deleted another run's runtime disk.");
    });

    await Check("startup retries only an authorized pending transient cleanup", async () =>
    {
        var fixture = Path.Combine(root, "recovery");
        var (config, paths) = await SetupAsync(fixture);
        var runtimeDirectory = Path.Combine(paths.Workspace, "Runtime", "orphan-run");
        Directory.CreateDirectory(runtimeDirectory);
        var disk = Path.Combine(runtimeDirectory, "xbox_hdd.qcow2");
        await File.WriteAllTextAsync(disk, "orphan");
        var result = Path.Combine(paths.Results, "orphan-run");
        Directory.CreateDirectory(result);
        await File.WriteAllTextAsync(Path.Combine(result, "runtime-cleanup.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            runId = "orphan-run",
            state = "pending",
            targetStopped = true,
            evidenceFinalized = true,
            runtimeDirectory,
            files = new[] { new { assetId = "xiso-empty-v1", path = "xbox_hdd.qcow2", retention = "deleteAfterEvidence", deleted = false } }
        }, ConfigLoader.JsonOptions));
        var engine = new RunnerEngine(config, paths);
        await engine.RunAsync(once: true, CancellationToken.None);
        Require(!File.Exists(disk), "Authorized orphan cleanup was not recovered.");
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result, "runtime-cleanup.json")));
        Require(receipt.RootElement.GetProperty("state").GetString() == "complete", "Recovered cleanup receipt was not completed.");
    });
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}

Console.WriteLine($"Disk asset checks: {5 - failures}/5 passed.");
return failures == 0 ? 0 : 1;

async Task<(RunnerConfig Config, RunnerPaths Paths)> SetupAsync(string fixture)
{
    Directory.CreateDirectory(fixture);
    var configPath = Path.Combine(fixture, "runner.json");
    var config = new RunnerConfig
    {
        Workspace = "workspace",
        Http = new HttpOptions { Enabled = false },
        Monitoring = new MonitoringOptions { Enabled = false },
        XemuControl = new XemuControlOptions { Enabled = false },
        Queue = new QueueOptions { PackageStabilityMs = 100, ScanIntervalMs = 25, FiniteWaitTimeoutSeconds = 2 },
        Reliability = new XemuTestRunner.Reliability.ReliabilityOptions
        {
            ProcessExitTimeoutMs = 3000,
            Preflight = new XemuTestRunner.Reliability.PreflightOptions { MinimumFreeSpaceBytes = 0 },
            Watchdog = new XemuTestRunner.Reliability.WatchdogOptions { Enabled = false }
        }
    };
    await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config, ConfigLoader.JsonOptions));
    var loaded = ConfigLoader.Load(configPath);
    return loaded;
}

string CreatePackage(string pending, string id, object job)
{
    var package = Path.Combine(pending, id);
    Directory.CreateDirectory(package);
    File.WriteAllText(Path.Combine(package, "job.json"), JsonSerializer.Serialize(job, ConfigLoader.JsonOptions));
    return package;
}

void CopySelf(string package)
{
    foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
        File.Copy(file, Path.Combine(package, Path.GetFileName(file)), overwrite: true);
    if (!OperatingSystem.IsWindows())
        File.SetUnixFileMode(Path.Combine(package, Path.GetFileName(Environment.ProcessPath!)),
            UnixFileMode.UserRead | UnixFileMode.UserExecute);
}

void WriteCatalogAsset(string workspace, string id, string kind, byte[] content, string sha)
{
    var directory = Path.Combine(workspace, "DiskAssets", id);
    Directory.CreateDirectory(directory);
    File.WriteAllBytes(Path.Combine(directory, "disk.qcow2"), content);
    File.WriteAllText(Path.Combine(directory, "asset.json"), JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        id,
        kind,
        length = content.LongLength,
        sha256 = sha,
        createdUtc = DateTimeOffset.UtcNow
    }, ConfigLoader.JsonOptions));
}
