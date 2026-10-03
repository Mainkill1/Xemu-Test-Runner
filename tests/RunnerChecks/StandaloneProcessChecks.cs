using System.Text.Json;
using System.Text.Json.Nodes;
using XemuTestRunner.Config;
using XemuTestRunner.Control;
using XemuTestRunner.Diagnostics;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;
using XemuTestRunner.Runtime;

internal static class StandaloneProcessChecks
{
    internal static async Task RunAsync(string root, string mode)
    {
        var fixture = Path.Combine(root, "standalone-" + mode);
        Directory.CreateDirectory(fixture);
        var configPath = Path.Combine(fixture, "runner.json");
        var config = new RunnerConfig
        {
            Workspace = "workspace",
            Queue = new QueueOptions { PackageStabilityMs = 100 },
            Diagnostics = new DiagnosticsOptions { Enabled = true, AutoFailureBundle = true },
            Http = new HttpOptions { Enabled = false },
            Monitoring = new MonitoringOptions { Enabled = false },
            XemuControl = new XemuControlOptions
            {
                Enabled = true, ConnectTimeoutMs = 500, InputProvider = "unavailable"
            },
            Reliability = new ReliabilityOptions
            {
                Preflight = new PreflightOptions { MinimumFreeSpaceBytes = 0 },
                ProcessExitTimeoutMs = 2000,
                Watchdog = new WatchdogOptions
                {
                    Enabled = true, StartupGraceMs = 0, IntervalMs = 100,
                    RequestTimeoutMs = 100, FailureThreshold = 1
                }
            }
        };
        await File.WriteAllTextAsync(configPath,
            JsonSerializer.Serialize(config, ConfigLoader.JsonOptions));
        var (_, paths) = ConfigLoader.Load(configPath);
        var package = Path.Combine(paths.Pending, "standalone");
        Directory.CreateDirectory(package);
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
            File.Copy(file, Path.Combine(package, Path.GetFileName(file)));
        var exe = Path.GetFileName(Environment.ProcessPath!);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Path.Combine(package, exe),
                UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var job = new JobDefinition
        {
            Id = "standalone", Executable = exe,
            TargetOs = OperatingSystem.IsWindows() ? "windows" : "linux",
            Arguments = ["--fixture-process", mode], TimeoutSeconds = 1,
            Operations = new OperationPolicyDefinition { Mode = "benchmark" },
            Experiment = new ExperimentDefinition { Id = "component-fixture", Variant = mode },
            Workload = new WorkloadContract
            {
                CorrectnessChecks = [new ArtifactCheckDefinition
                {
                    Name = "output", Path = "stdout.log", ContainsText = "\"passed\":true"
                }],
                EvidenceRequirements = [new ArtifactCheckDefinition
                {
                    Name = "stdout", Path = "stdout.log", MinimumBytes = 2
                }],
                ReportedMetrics = [new ReportedMetricDefinition
                {
                    Name = "fixture-value", Path = "stdout.log", JsonProperty = "fixtureValue",
                    Unit = "fixture", Direction = "lower"
                }]
            }
        };
        var document = JsonSerializer.SerializeToNode(job, ConfigLoader.JsonOptions)!;
        document["TargetKind"] = "process";
        await File.WriteAllTextAsync(Path.Combine(package, "job.json"), document.ToJsonString());
        var engine = new RunnerEngine(config, paths);
        await engine.RunAsync(once: true, CancellationToken.None);
        var resultDirectory = Directory.GetDirectories(paths.Results).Single();
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(resultDirectory, "result.json")));
        Assert(result.RootElement.GetProperty("automaticDiagnostic").ValueKind == JsonValueKind.Null &&
               result.RootElement.GetProperty("automaticDiagnosticError").ValueKind == JsonValueKind.Null,
            "Process failure invoked xemu automatic diagnostics.");
        if (mode == "timeout")
            Assert(result.RootElement.GetProperty("detail").GetString()!.Contains("wall-clock"),
                "Process timeout clock is mislabeled.");
        var expected = mode switch { "success" => "completed", "failure" => "failed", _ => "timeout" };
        Assert(result.RootElement.GetProperty("status").GetString() == expected,
            result.RootElement.ToString());
        using var launch = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(resultDirectory, "launch.json")));
        Assert(launch.RootElement.GetProperty("arguments").GetArrayLength() == 2,
            "Standalone target arguments gained QMP options.");
        Assert(launch.RootElement.GetProperty("targetKind").GetString() == "process",
            "Launch evidence lost the target kind.");
        Assert(Directory.GetDirectories(paths.Tested).Length == 1 &&
               Directory.GetDirectories(paths.Testing).Length == 0, "Package did not archive.");
        if (mode == "success")
        {
            Assert(result.RootElement.GetProperty("assessment").GetProperty("Comparison")
                .GetString() == "eligible", "Successful process contract is not eligible.");
            Assert(result.RootElement.GetProperty("workload").GetProperty("Measurements")[0]
                .GetProperty("Value").GetDouble() == 1234, "Reported metric was not retained.");
        }
        else if (mode == "failure")
            Assert(result.RootElement.GetProperty("exitCode").GetInt32() == 17,
                "Nonzero process exit was lost.");
    }

    internal static void ValidateDefinition(string root)
    {
        var package = Path.Combine(root, "standalone-definitions");
        Directory.CreateDirectory(package);
        Assert(!JsonSerializer.Serialize(new JobDefinition(), ConfigLoader.JsonOptions)
            .Contains("TargetKind", StringComparison.Ordinal),
            "Default xemu jobs changed their saved-definition fingerprint.");
        ValidateSceneGateComposition(package);
        foreach (var extra in new[]
        {
            "\"TargetKind\":\"unknown\"", "\"TargetKind\":null", "\"TargetKind\":2", "\"TargetKind\":1",
            "\"TargetKind\":\"1\"", "\"TargetKind\":\" process \"",
            "\"TargetKind\":\"process\",\"StartPaused\":true",
            "\"TargetKind\":\"process\",\"RequireInput\":true",
            "\"TargetKind\":\"process\",\"SnapshotName\":\"save\"",
            "\"TargetKind\":\"process\",\"Plan\":[{\"Type\":\"quit\"}]"
        })
        {
            File.WriteAllText(Path.Combine(package, "job.json"),
                "{\"Id\":\"definition\",\"Executable\":\"tool\"," + extra + "}");
            bool rejected = false;
            try { _ = JobDefinition.LoadPackage(package); }
            catch (Exception error) when (error is InvalidDataException or JsonException) { rejected = true; }
            Assert(rejected, "Unsupported standalone definition was accepted: " + extra);
        }
    }

    private static void ValidateSceneGateComposition(string package)
    {
        var manifest = new JsonObject
        {
            ["Id"] = "definition", ["Executable"] = "tool",
            ["TargetKind"] = "process",
            ["Operations"] = new JsonObject { ["Mode"] = "benchmark" }
        };
        var path = Path.Combine(package, "job.json");
        File.WriteAllText(path, manifest.ToJsonString());
        Assert(JobDefinition.LoadPackage(package).TargetKind == JobTargetKind.Process,
            "Standalone benchmark without guest input was rejected.");

        manifest["RequireInput"] = true;
        manifest["Plan"] = new JsonArray(
            new JsonObject { ["Type"] = "segment_start", ["Name"] = "measurement" },
            new JsonObject { ["Type"] = "segment_end", ["Name"] = "measurement" });
        foreach (var explicitXemu in new[] { false, true })
        {
            if (explicitXemu) manifest["TargetKind"] = "xemu";
            else manifest.Remove("TargetKind");
            File.WriteAllText(path, manifest.ToJsonString());
            bool rejectedForScene = false;
            try { _ = JobDefinition.LoadPackage(package); }
            catch (InvalidDataException error)
            {
                rejectedForScene = error.Message.Contains("measurement-start scene",
                    StringComparison.OrdinalIgnoreCase);
            }
            Assert(rejectedForScene,
                $"{(explicitXemu ? "Explicit" : "Default")} xemu input benchmark bypassed the scene gate.");
        }
    }

    internal static void RejectDiagnosticAttachment(string root)
    {
        using var control = new XemuControlManager(new XemuControlOptions());
        var hub = new DiagnosticHub(new DiagnosticsOptions(), control,
            new RunnerState(), new ActivityHub());
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        bool rejected = false;
        try
        {
            hub.Attach("process", process, null, root,
                new JobDefinition { TargetKind = JobTargetKind.Process }, CancellationToken.None);
        }
        catch (InvalidOperationException) { rejected = true; }
        Assert(rejected && !hub.Snapshot().Active, "Process target attached to xemu diagnostic hub.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
