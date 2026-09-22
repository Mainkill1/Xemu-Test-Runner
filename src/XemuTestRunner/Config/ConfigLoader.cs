using System.Text.Json;

namespace XemuTestRunner.Config;

public static class ConfigLoader
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true, WriteIndented = true
    };
    public static (RunnerConfig Config, RunnerPaths Paths) Load(string path)
    {
        var full = Path.GetFullPath(path);
        var config = JsonSerializer.Deserialize<RunnerConfig>(File.ReadAllText(full), JsonOptions)
            ?? throw new InvalidDataException("Empty runner config.");
        Validate(config);
        var paths = ResolvePaths(config, full);
        var states = new[] { paths.Pending, paths.Testing, paths.Tested, paths.Results };
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var i = 0; i < states.Length; i++)
            for (var j = 0; j < states.Length; j++)
                if (i != j && (states[i].Equals(states[j], cmp) || states[i].StartsWith(states[j].TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, cmp)))
                    throw new InvalidDataException("Queue state and result directories must be distinct and not nested.");
        return (config, paths);
    }
    public static void WriteExample(string path, bool overwrite)
    {
        var full = Path.GetFullPath(path);
        if (File.Exists(full) && !overwrite) throw new IOException("Config already exists: " + full);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, JsonSerializer.Serialize(new RunnerConfig(), JsonOptions));
    }
    public static RunnerPaths ResolvePaths(RunnerConfig config, string configFile)
    {
        var full = Path.GetFullPath(configFile);
        var workspace = Resolve(Path.GetDirectoryName(full)!, config.Workspace);
        return new(full, workspace, Resolve(workspace, config.Queue.Pending), Resolve(workspace, config.Queue.Testing),
            Resolve(workspace, config.Queue.Tested), Resolve(workspace, config.Queue.Results), Resolve(workspace, config.Http.FileRoot));
    }
    private static string Resolve(string root, string value) => Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(root, value));
    private static void Validate(RunnerConfig c)
    {
        if (c.Queue is null || c.Monitoring is null || c.Http is null || c.XemuControl is null || c.Ui is null || c.Reliability is null ||
            c.Diagnostics is null || c.Monitoring.Gpu is null || c.Reliability.Preflight is null ||
            c.Reliability.Watchdog is null || c.XemuControl.ButtonKeys is null ||
            c.XemuControl.ScreenshotArguments is null)
            throw new InvalidDataException("Configuration sections cannot be null.");
        if (c.Monitoring.IntervalMs <= 0 ||
            c.Monitoring.FlushIntervalMs <= 0 ||
            c.Monitoring.BufferCapacity < 16 ||
            c.Queue.ScanIntervalMs <= 0 ||
            c.Queue.PackageStabilityMs < 100)
            throw new InvalidDataException(
                "Sampling/flush/queue intervals must be positive; PackageStabilityMs must be at least 100; buffer capacity must be at least 16.");
        if (c.Monitoring.Gpu.SampleIntervalMs < c.Monitoring.IntervalMs ||
            c.Monitoring.Gpu.SensorIntervalMs < c.Monitoring.Gpu.SampleIntervalMs ||
            c.Monitoring.Gpu.CounterRefreshMs < c.Monitoring.Gpu.SampleIntervalMs)
            throw new InvalidDataException(
                "GPU SampleIntervalMs must be >= Monitoring.IntervalMs; SensorIntervalMs and CounterRefreshMs must be >= SampleIntervalMs.");
        if (c.Http.Port is < 1 or > 65535 || c.Http.TransferBufferBytes is < 65536 or > 16777216 || c.Http.MaxHeaderBytes is < 4096 or > 1048576)
            throw new InvalidDataException("Invalid HTTP port or buffer/header size.");
        if (c.XemuControl.QmpPort is < 0 or > 65535 || c.XemuControl.ConnectTimeoutMs <= 0 || c.XemuControl.ScreenshotTimeoutMs <= 0 ||
            c.XemuControl.DefaultButtonHoldMs is < 1 or > 60000)
            throw new InvalidDataException("Invalid xemu control port, timeout, or button duration.");
        var screenshotProvider = c.XemuControl.ScreenshotProvider?.Trim().ToLowerInvariant();
        if (screenshotProvider is not ("auto" or "qmp" or "external"))
            throw new InvalidDataException("ScreenshotProvider must be auto, qmp, or external.");
        if (screenshotProvider == "external" && string.IsNullOrWhiteSpace(c.XemuControl.ScreenshotExecutable))
            throw new InvalidDataException("External screenshots require ScreenshotExecutable.");
        if (c.XemuControl.ScreenshotArguments.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("ScreenshotArguments cannot contain empty values.");
        if (!string.IsNullOrWhiteSpace(c.XemuControl.ScreenshotExecutable) &&
            !c.XemuControl.ScreenshotArguments.Any(argument => argument.Contains("{path}", StringComparison.Ordinal)))
            throw new InvalidDataException("External screenshot arguments must contain the {path} placeholder.");
        if (c.Ui.CliRefreshMs <= 0 || c.Ui.WebRefreshMs <= 0 || c.Ui.LivePreviewIntervalMs < 250)
            throw new InvalidDataException("UI refresh intervals must be positive; preview interval must be at least 250 ms.");
        if (c.Queue.InterruptedAction?.ToLowerInvariant() is not ("retry" or "hold")) throw new InvalidDataException("InterruptedAction must be retry or hold.");
        var r = c.Reliability; var w = r.Watchdog;
        if (r.MaxInterruptedRetries is < 0 or > 100 || r.ProcessExitTimeoutMs <= 0 || r.Preflight.MinimumFreeSpaceBytes < 0 ||
            r.EvidenceListLimit is < 1 or > 200 || r.MaxPreviewBytes is < 1024 or > 67108864 || w.StartupGraceMs < 0 ||
            w.IntervalMs < 100 || w.RequestTimeoutMs < 100 || w.FailureThreshold is < 1 or > 100)
            throw new InvalidDataException("Invalid reliability limits or watchdog intervals.");
        if (c.Diagnostics.ToolTimeoutMs < 100 || c.Diagnostics.CaptureFinalizeTimeoutMs < 1000)
            throw new InvalidDataException("Diagnostic tool timeout must be at least 100 ms and capture finalization at least 1000 ms.");
        if (new[]
        {
            c.Diagnostics.WprExecutable, c.Diagnostics.XperfExecutable, c.Diagnostics.PerfExecutable,
            c.Diagnostics.GdbExecutable, c.Diagnostics.ProcDumpExecutable, c.Diagnostics.RenderDocCommand,
            c.Diagnostics.PythonExecutable, c.Diagnostics.Addr2LineExecutable
        }.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Diagnostic executable settings cannot be empty.");
        // JSON deserialization may replace the original case-insensitive dictionary.
        c.XemuControl.ButtonKeys = new(c.XemuControl.ButtonKeys, StringComparer.OrdinalIgnoreCase);
    }
}
