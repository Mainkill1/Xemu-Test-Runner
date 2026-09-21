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

            try
            {
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
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
            {
                // A service may outlive the terminal that launched it. Spectre's
                // live renderer then loses its Windows console handle while the
                // queue and HTTP service are still healthy. Continue headless;
                // the independently started engine remains authoritative.
            }

            runTask.GetAwaiter().GetResult();
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
            }
            catch (Exception displayError) when (displayError is IOException or InvalidOperationException or NotSupportedException)
            {
                try { Console.Error.WriteLine(ex); }
                catch (IOException) { }
            }
            return 1;
        }
    }
}
