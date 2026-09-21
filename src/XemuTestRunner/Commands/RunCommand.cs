using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using XemuTestRunner.Config;
using XemuTestRunner.Runtime;

namespace XemuTestRunner.Commands;

public sealed class RunCommandSettings : CommandSettings
{
    [CommandOption("-c|--config <PATH>")]
    [Description("Path to runner JSON configuration.")]
    public string ConfigPath { get; set; } = "runner.json";

    [CommandOption("--once")]
    [Description("Process the current queue and exit when it becomes empty.")]
    public bool Once { get; set; }
}

public sealed class RunCommand : Command<RunCommandSettings>
{
    protected override int Execute(
        CommandContext context,
        RunCommandSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            var (config, paths) = ConfigLoader.Load(settings.ConfigPath);
            var engine = new RunnerEngine(config, paths);
            var runTask = engine.RunAsync(settings.Once, cancellationToken);

            AnsiConsole.Live(CliDashboard.Build(engine.State.Snapshot()))
                .AutoClear(false)
                .StartAsync(async live =>
                {
                    try
                    {
                        while (!runTask.IsCompleted)
                        {
                            live.UpdateTarget(CliDashboard.Build(engine.State.Snapshot()));
                            live.Refresh();
                            await Task.Delay(config.Ui.CliRefreshMs, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                    }
                    finally
                    {
                        live.UpdateTarget(CliDashboard.Build(engine.State.Snapshot()));
                        live.Refresh();
                    }
                })
                .GetAwaiter()
                .GetResult();

            runTask.GetAwaiter().GetResult();
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
            return 1;
        }
    }
}
