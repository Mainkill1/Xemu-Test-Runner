using Tomlyn;
using Tomlyn.Model;
using XemuTestRunner.Diagnostics;
using XemuTestRunner.Queue;
using XemuTestRunner.Runtime;

internal static class ProcessStateChecks
{
    public static async Task<int> Child(string[] args)
    {
        var index = Array.IndexOf(args, "-config_path");
        if (index < 0 || index + 1 >= args.Length) return 11;
        var config = Toml.ToModel(await File.ReadAllTextAsync(args[index + 1]));
        if (((TomlTable)config["perf"])["cache_shaders"] is not false) return 12;
        var cache = Path.Combine(AppContext.BaseDirectory, "cache");
        Directory.CreateDirectory(cache);
        await File.WriteAllTextAsync(Path.Combine(cache, "child-cache.bin"), "produced-by-child");
        await File.AppendAllTextAsync(args[index + 1], "\n# child saved config\n");
        return 0;
    }

    public static void Register(List<(string, Func<Task>)> checks)
    {
        checks.Add(("actual target launch uses private config and finalizes state after process exit", async () =>
        {
            using var f = new Fixture();
            var image = Environment.ProcessPath ?? throw new InvalidOperationException("No apphost path.");
            Fixture.Require(!Path.GetFileNameWithoutExtension(image).Equals("dotnet", StringComparison.OrdinalIgnoreCase), "This fixture requires its generated .NET apphost.");
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
                File.Copy(file, Path.Combine(f.Package, Path.GetFileName(file)), true);
            var executable = Path.Combine(f.Package, Path.GetFileName(image));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
            var original = await File.ReadAllTextAsync(f.Config);
            var job = new JobDefinition
            {
                Id = "real-state-child", Executable = Path.GetFileName(image),
                RuntimeState = new() { Isolation = new() { CacheMode = "cold", CacheShaders = false, RequirePrivateGuestState = false } }
            };
            var options = new DiagnosticsOptions();
            options.CrashReports.PreserveNativeExitStatus = false;
            await using (var target = await TargetLaunch.StartAsync(options, job, executable, f.Package,
                ["--state-child"], new Dictionary<string, string>(), f.Result, CancellationToken.None))
            {
                await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Fixture.Require(target.ExitCode == 0, "Child did not receive the effective config.");
            }
            var report = RunStateQualification.Read(f.Result)!;
            Fixture.Require(report.TargetStopped && report.Status == "complete", "Launch disposal did not finalize stopped-target state.");
            Fixture.Require(report.After!.Files.Any(file => file.Path == "cache/child-cache.bin"), "Actual target cache output was not recorded.");
            Fixture.Require(report.ConfigAfterSha256 != report.EffectiveConfigSha256, "Actual target config write was not retained.");
            Fixture.Require(await File.ReadAllTextAsync(f.Config) == original, "Target wrote the original configuration.");
        }));
    }
}
