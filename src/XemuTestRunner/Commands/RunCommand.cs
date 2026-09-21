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
    protected override int Execute(CommandContext context, RunCommandSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var (config, paths) = ConfigLoader.Load(settings.ConfigPath);
            AnsiConsole.MarkupLine($"[grey]Workspace:[/] {Markup.Escape(paths.Workspace)}");
            AnsiConsole.MarkupLine($"[grey]Monitoring:[/] {(config.Monitoring.Enabled ? $"{config.Monitoring.IntervalMs} ms" : "disabled")}");
            if (config.Http.Enabled)
                AnsiConsole.MarkupLine($"[grey]HTTP:[/] http://{Markup.Escape(config.Http.BindAddress)}:{config.Http.Port}");
            AnsiConsole.MarkupLine("[grey]Press Ctrl+C to stop.[/]");

            new RunnerEngine(config, paths).RunAsync(settings.Once, cancellationToken).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
            return 1;
        }
    }
}
