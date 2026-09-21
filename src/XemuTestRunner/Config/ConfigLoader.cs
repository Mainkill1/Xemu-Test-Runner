using System.Text.Json;

namespace XemuTestRunner.Config;

public static class ConfigLoader
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    public static (RunnerConfig Config, RunnerPaths Paths) Load(string path)
    {
        var configFile = Path.GetFullPath(path);
        if (!File.Exists(configFile))
            throw new FileNotFoundException($"Runner config not found: {configFile}");

        var config = JsonSerializer.Deserialize<RunnerConfig>(File.ReadAllText(configFile), JsonOptions)
            ?? throw new InvalidDataException("Runner config was empty or invalid.");

        Validate(config);
        return (config, ResolvePaths(config, configFile));
    }

    public static void WriteExample(string path, bool overwrite)
    {
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath) && !overwrite)
            throw new IOException($"Config already exists: {fullPath}");

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(new RunnerConfig(), JsonOptions));
    }

    public static RunnerPaths ResolvePaths(RunnerConfig config, string configFile)
    {
        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(configFile))!;
        var workspace = Resolve(baseDirectory, config.Workspace);

        return new RunnerPaths(
            Path.GetFullPath(configFile),
            workspace,
            Resolve(workspace, config.Queue.Pending),
            Resolve(workspace, config.Queue.Testing),
            Resolve(workspace, config.Queue.Tested),
            Resolve(workspace, config.Queue.Results),
            Resolve(workspace, config.Http.FileRoot));
    }

    private static string Resolve(string root, string value) =>
        Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(root, value));

    private static void Validate(RunnerConfig config)
    {
        if (config.Monitoring.IntervalMs <= 0)
            throw new InvalidDataException("Monitoring.IntervalMs must be greater than zero.");
        if (config.Monitoring.FlushIntervalMs <= 0)
            throw new InvalidDataException("Monitoring.FlushIntervalMs must be greater than zero.");
        if (config.Monitoring.BufferCapacity < 16)
            throw new InvalidDataException("Monitoring.BufferCapacity must be at least 16.");
        if (config.Queue.ScanIntervalMs <= 0)
            throw new InvalidDataException("Queue.ScanIntervalMs must be greater than zero.");
        if (config.Http.Port is < 1 or > 65535)
            throw new InvalidDataException("Http.Port must be between 1 and 65535.");
        if (config.Http.TransferBufferBytes < 64 * 1024)
            throw new InvalidDataException("Http.TransferBufferBytes must be at least 65536 bytes.");
        if (config.Http.MaxHeaderBytes < 4096)
            throw new InvalidDataException("Http.MaxHeaderBytes must be at least 4096 bytes.");
        if (config.XemuControl.QmpPort is < 0 or > 65535)
            throw new InvalidDataException("XemuControl.QmpPort must be 0 (automatic) or between 1 and 65535.");
        if (config.XemuControl.ConnectTimeoutMs <= 0)
            throw new InvalidDataException("XemuControl.ConnectTimeoutMs must be greater than zero.");
        if (config.XemuControl.ScreenshotTimeoutMs <= 0)
            throw new InvalidDataException("XemuControl.ScreenshotTimeoutMs must be greater than zero.");
        if (config.XemuControl.DefaultButtonHoldMs <= 0)
            throw new InvalidDataException("XemuControl.DefaultButtonHoldMs must be greater than zero.");
        if (config.Ui.CliRefreshMs <= 0)
            throw new InvalidDataException("Ui.CliRefreshMs must be greater than zero.");
        if (config.Ui.WebRefreshMs <= 0)
            throw new InvalidDataException("Ui.WebRefreshMs must be greater than zero.");
        if (config.Ui.LivePreviewIntervalMs < 250)
            throw new InvalidDataException("Ui.LivePreviewIntervalMs must be at least 250 ms.");

        var interrupted = config.Queue.InterruptedAction.ToLowerInvariant();
        if (interrupted is not ("retry" or "hold"))
            throw new InvalidDataException("Queue.InterruptedAction must be 'retry' or 'hold'.");
    }
}
