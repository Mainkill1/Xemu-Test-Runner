using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Queue;
using XemuTestRunner.Runtime;

var checks = new List<(string, Func<Task>)>
{
    ("cold start preserves old cache but does not expose it to the next run", async () =>
    {
        using var f = new Fixture();
        Directory.CreateDirectory(Path.Combine(f.Package, "cache", "vulkan"));
        await File.WriteAllTextAsync(Path.Combine(f.Package, "cache", "vulkan", "old.bin"), "previous-attempt");
        var session = await f.Prepare("cold");
        Fixture.Require(!File.Exists(Path.Combine(f.Package, "cache", "vulkan", "old.bin")), "Old cache is still visible to the target.");
        Fixture.Require(Directory.EnumerateFiles(f.Result, "old.bin", SearchOption.AllDirectories).Any(), "Cold reset destroyed prior evidence.");
        Fixture.Require(session.Report.Before!.Files.Count == 0 && session.Report.Before.Complete, "Cold initial state was not empty.");
        await session.CompleteAsync(true);
    }),
    ("effective config has explicit shader setting and does not overwrite the source", async () =>
    {
        using var f = new Fixture();
        var original = await File.ReadAllTextAsync(f.Config);
        var session = await f.Prepare("cold", shaders: false);
        var effective = await File.ReadAllTextAsync(session.Report.EffectiveConfigPath!);
        Fixture.Require(effective.Contains("cache_shaders = false"), "Executable default still selects shader caching.");
        Fixture.Require(await File.ReadAllTextAsync(f.Config) == original, "The source configuration changed.");
        Fixture.Require(session.Arguments.Count(x => x == "-config_path") == 1, "Config override is missing or duplicated.");
        Fixture.Require(session.Report.ConfigSha256 == Fixture.Hash(original), "Original config identity was lost.");
        await session.CompleteAsync(true);
    }),
    ("seeded cache copies exact pinned content into private writable state", async () =>
    {
        using var f = new Fixture();
        var seed = Path.Combine(f.Package, "seed");
        Directory.CreateDirectory(seed);
        await File.WriteAllTextAsync(Path.Combine(seed, "warm.bin"), "warm-cache");
        var snapshot = await RunStateInventory.CaptureAsync(seed, 100, 1024 * 1024, CancellationToken.None);
        var session = await f.Prepare("seeded", seed: "seed", digest: snapshot.TreeSha256);
        Fixture.Require(session.Report.Before!.TreeSha256 == snapshot.TreeSha256, "Seeded bytes changed during materialization.");
        await File.WriteAllTextAsync(Path.Combine(f.Package, "cache", "warm.bin"), "changed-by-target");
        Fixture.Require(await File.ReadAllTextAsync(Path.Combine(seed, "warm.bin")) == "warm-cache", "Target cache shares writable seed storage.");
        await session.CompleteAsync(true);
        Fixture.Require(session.Report.After!.TreeSha256 != snapshot.TreeSha256, "Final cache changes were not inventoried.");
    }),
    ("wrong seed digest is rejected before replacing cache", async () =>
    {
        using var f = new Fixture();
        Directory.CreateDirectory(Path.Combine(f.Package, "seed"));
        await File.WriteAllTextAsync(Path.Combine(f.Package, "seed", "a"), "seed");
        await Fixture.Reject(async () => { await f.Prepare("seeded", seed: "seed", digest: new string('0', 64)); });
    }),
    ("cold and seeded policies have distinct comparison identities", async () =>
    {
        using var cold = new Fixture();
        using var seeded = new Fixture();
        var empty = Path.Combine(seeded.Package, "seed");
        Directory.CreateDirectory(empty);
        var digest = (await RunStateInventory.CaptureAsync(empty, 10, 1024, CancellationToken.None)).TreeSha256;
        var a = await cold.Prepare("cold");
        var b = await seeded.Prepare("seeded", seed: "seed", digest: digest);
        Fixture.Require(a.Report.ContractSha256 != b.Report.ContractSha256, "Different initial-state contracts compare as identical.");
        await a.CompleteAsync(true); await b.CompleteAsync(true);
    }),
    ("inherited state and uncontrolled driver cache cannot claim clean qualification", async () =>
    {
        using var f = new Fixture();
        var session = await f.Prepare("inherited");
        await session.CompleteAsync(true);
        Fixture.Require(!session.Report.ComparisonReady, "Inherited state was qualified as controlled.");
        Fixture.Require(session.Report.Uncontrolled.Contains("driver-cache"), "Uncontrolled driver cache is hidden.");
        Fixture.Require(session.Report.Uncontrolled.Contains("os-page-cache"), "Application cache was confused with OS page cache.");
    }),
    ("inventory limits report incomplete state instead of a false empty cache", async () =>
    {
        using var f = new Fixture();
        var root = Path.Combine(f.Package, "many");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "a"), "12345678");
        await File.WriteAllTextAsync(Path.Combine(root, "b"), "12345678");
        var inventory = await RunStateInventory.CaptureAsync(root, 1, 4, CancellationToken.None);
        Fixture.Require(!inventory.Complete && inventory.Issues.Count > 0, "Limited inventory implies complete state.");
    }),
    ("cache tree symlinks are refused without reading their targets", async () =>
    {
        using var f = new Fixture();
        var root = Path.Combine(f.Package, "linked");
        Directory.CreateDirectory(root);
        try { File.CreateSymbolicLink(Path.Combine(root, "escape"), f.Config); }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { Console.WriteLine("SKIP symbolic-link creation requires Windows privilege."); return; }
        var inventory = await RunStateInventory.CaptureAsync(root, 10, 1024 * 1024, CancellationToken.None);
        Fixture.Require(!inventory.Complete && inventory.Files.All(x => x.Path != "escape"), "Linked outside content was inventoried.");
    }),
    ("missing isolation section leaves previous canonical JSON unchanged", () =>
    {
        var json = JsonSerializer.Serialize(new RuntimeStateDefinition(), ConfigLoader.JsonOptions);
        Fixture.Require(!json.Contains("Isolation", StringComparison.OrdinalIgnoreCase), "Optional isolation changed all saved definition identities.");
        return Task.CompletedTask;
    }),
    ("post-run state has a bounded summary and preserves changed effective config", async () =>
    {
        using var f = new Fixture();
        var session = await f.Prepare("cold");
        await File.AppendAllTextAsync(session.Report.EffectiveConfigPath!, "\n# saved by application\n");
        await session.CompleteAsync(true);
        Fixture.Require(session.Report.ConfigAfterSha256 != session.Report.EffectiveConfigSha256, "Saved config changes were hidden.");
        Fixture.Require(File.Exists(Path.Combine(f.Result, "diagnostics", "run-state", "report.json")), "State metadata will not be included in diagnostics.");
        Fixture.Require(File.Exists(Path.Combine(f.Result, "diagnostics", "run-state", "config-after.toml")), "Changed configuration is not retained.");
    }),
    ("managed config cannot select an external writable guest image", async () =>
    {
        using var f = new Fixture();
        await File.AppendAllTextAsync(f.Config, "\n[sys.files]\nhdd_path = 'external-hdd.qcow2'\n");
        await Fixture.Reject(async () => { await f.Prepare("cold"); });
    }),
    ("different run directories do not change an otherwise identical state contract", async () =>
    {
        using var a = new Fixture(); using var b = new Fixture();
        var first = await a.Prepare("cold"); var second = await b.Prepare("cold");
        Fixture.Require(first.Report.ContractSha256 == second.Report.ContractSha256, "Absolute run paths leak into comparison identity.");
        await first.CompleteAsync(true); await second.CompleteAsync(true);
    })
};
var failures = 0;
foreach (var (name, run) in checks)
{
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + e); }
}
Console.WriteLine($"State checks: {checks.Count - failures}/{checks.Count} passed.");
return failures == 0 ? 0 : 1;

sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "run-state-" + Guid.NewGuid().ToString("N"));
    public string Package => Path.Combine(Root, "package");
    public string Result => Path.Combine(Root, "result");
    public string Config => Path.Combine(Package, "xemu.toml");
    public Fixture()
    {
        Directory.CreateDirectory(Package); Directory.CreateDirectory(Result);
        File.WriteAllText(Path.Combine(Package, "xemu.bin"), "fixture-binary");
        File.WriteAllText(Config, "[perf]\nhard_fpu = true\n");
    }
    public async Task<RunStorageSession> Prepare(string mode, bool shaders = true, string? seed = null, string? digest = null)
    {
        var json = JsonSerializer.Serialize(new { Id = "state-check", Executable = "xemu.bin", RuntimeState = new {
            Isolation = new { CacheMode = mode, CacheShaders = shaders, DriverCache = "uncontrolled", SeedDirectory = seed, SeedSha256 = digest }
        }});
        var job = JsonSerializer.Deserialize<JobDefinition>(json, ConfigLoader.JsonOptions)!;
        return await RunStorageSession.PrepareAsync(job, Path.Combine(Package, "xemu.bin"), Package,
            ["-config_path", Config], new Dictionary<string, string>(), Result, CancellationToken.None);
    }
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    public static async Task Reject(Func<Task> operation)
    {
        try { await operation(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid state contract was accepted.");
    }
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}
