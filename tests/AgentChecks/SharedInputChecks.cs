using System.Net;
using System.Text;
using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;
using XemuTestRunner.Runtime;
using static AgentFixture;

internal static class SharedInputChecks
{
    private static object Policy(string sha, string field = "bootrom_path") => new
    {
        CacheMode = "cold", CacheShaders = true, AllowUncontrolledDriverCache = true,
        ReadOnlyAssets = new[] { new { Field = field, AssetId = "shared-boot", ExpectedSha256 = sha } }
    };
    private static async Task<(JobDefinition Job, string Executable, string Result, string Shared)> Setup(AgentFixture host, string bytes = "boot-rom")
    {
        host.State.SetPhase("fixture");
        var sha = Digest(bytes);
        await host.Json("/api/v1/disk-assets", HttpMethod.Post, new { id = "shared-boot", kind = "readonly-input", length = bytes.Length, sha256 = sha });
        using (var body = new ByteArrayContent(Encoding.UTF8.GetBytes(bytes)))
        using (var response = await host.Client.PutAsync("/api/v1/disk-assets/shared-boot/content", body)) Require(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var package = Path.Combine(host.Root, "managed-package"); Directory.CreateDirectory(package);
        var executable = Path.Combine(package, "xemu"); await File.WriteAllTextAsync(executable, "fixture");
        await File.WriteAllTextAsync(Path.Combine(package, "xemu.toml"), "[sys.files]\nbootrom_path = 'selected-by-catalog'\nhdd_path = '{runtimeDir}/hdd.qcow2'\neeprom_path = '{runtimeDir}/eeprom.bin'\n");
        await File.WriteAllTextAsync(Path.Combine(package, "job.json"), JsonSerializer.Serialize(new { Id = "shared-run", Executable = "xemu", RuntimeState = new { Enabled = true, Isolation = Policy(sha) } }));
        var job = JobDefinition.LoadPackage(package);
        var result = Path.Combine(host.Paths.Results, "shared-run"); Directory.CreateDirectory(result);
        var runtime = Path.Combine(host.Paths.Workspace, "Runtime", "shared-run"); Directory.CreateDirectory(runtime);
        await File.WriteAllTextAsync(Path.Combine(runtime, "hdd.qcow2"), "disk"); await File.WriteAllTextAsync(Path.Combine(runtime, "eeprom.bin"), "eeprom");
        // Match the actual RunnerEngine receipt, including enabled and lower-case envelope fields.
        AtomicJson.Write(Path.Combine(result, "runtime-state.json"), new
        {
            enabled = true, directory = runtime, files = new[]
            {
                new RuntimeFileMaterialization("hdd-seed", "hdd.qcow2", 4, Digest("disk")),
                new RuntimeFileMaterialization("eeprom-seed", "eeprom.bin", 6, Digest("eeprom"))
            }
        });
        return (job, executable, result, Path.Combine(host.Paths.Workspace, "DiskAssets", "shared-boot", "disk.qcow2"));
    }
    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("managed state resolves pinned shared boot inputs without copying them into each package", async () =>
        {
            await using var host = new AgentFixture(); var f = await Setup(host);
            var session = await RunStorageSession.PrepareAsync(f.Job, f.Executable, f.Job.PackageDirectory!, [], new Dictionary<string,string>(), f.Result, CancellationToken.None);
            Require(session.Report.StoragePaths["bootrom_path"] == f.Shared, "Configuration did not point to the runner-owned catalog.");
            Require(!Directory.EnumerateFiles(f.Job.PackageDirectory!, "*", SearchOption.AllDirectories).Any(file => File.ReadAllText(file) == "boot-rom"), "Shared boot input was duplicated into the package.");
            Require(session.Arguments.Count(argument => argument == "-config_path") == 1, "More than one effective configuration was handed to the target.");
            await session.CompleteAsync(true);
            Require(session.Report.ComparisonReady, string.Join(";", session.Report.Issues));
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(f.Result, "diagnostics", "run-state", "report.json")));
            var asset = report.RootElement.GetProperty("ReadOnlyAssets")[0];
            Require(asset.GetProperty("BeforeSha256").GetString() == Digest("boot-rom") && asset.GetProperty("AfterSha256").GetString() == Digest("boot-rom"), "Actual shared input identity is not retained.");
        }));
        checks.Add(("shared input tampering blocks launch before cache mutation", async () =>
        {
            await using var host = new AgentFixture(); var f = await Setup(host);
            await File.WriteAllTextAsync(f.Shared, "tampered");
            var cache = Path.Combine(f.Job.PackageDirectory!, "cache"); Directory.CreateDirectory(cache); await File.WriteAllTextAsync(Path.Combine(cache, "prior"), "keep-me");
            var rejected = false;
            try { await RunStorageSession.PrepareAsync(f.Job, f.Executable, f.Job.PackageDirectory!, [], new Dictionary<string,string>(), f.Result, CancellationToken.None); }
            catch (InvalidDataException error) { rejected = error.Message.Contains("SHA-256", StringComparison.Ordinal); }
            Require(rejected, "Incorrect shared bytes passed preparation.");
            Require(File.Exists(Path.Combine(cache, "prior")), "Shared input validation occurred only after the cache was changed.");
        }));
        checks.Add(("shared input changes during a run invalidate its state ledger", async () =>
        {
            await using var host = new AgentFixture(); var f = await Setup(host);
            var session = await RunStorageSession.PrepareAsync(f.Job, f.Executable, f.Job.PackageDirectory!, [], new Dictionary<string,string>(), f.Result, CancellationToken.None);
            await File.WriteAllTextAsync(f.Shared, "tampered"); await session.CompleteAsync(true);
            Require(!session.Report.ComparisonReady && session.Report.Status == "incomplete", "Changed shared input remained comparison-ready.");
        }));
        checks.Add(("mutable guest disks cannot be assigned a shared read-only role", async () =>
        {
            foreach (var field in new[] { "hdd_path", "eeprom_path", "../bootrom_path" })
            {
                var rejected = false;
                try { JsonSerializer.Deserialize<RunIsolationDefinition>(JsonSerializer.Serialize(Policy(new string('a', 64), field)), ConfigLoader.JsonOptions); }
                catch (Exception error) when (error is InvalidDataException or JsonException) { rejected = true; }
                Require(rejected, "Writable guest state accepted shared role " + field);
            }
            await Task.CompletedTask;
        }));
        checks.Add(("legacy isolation serialization is unchanged when no shared assets are selected", async () =>
        {
            var raw = JsonSerializer.Serialize(new RunIsolationDefinition(), ConfigLoader.JsonOptions);
            Require(!raw.Contains("ReadOnlyAssets", StringComparison.Ordinal), "New empty policy rewrote every frozen test hash.");
            await Task.CompletedTask;
        }));
    }
}
