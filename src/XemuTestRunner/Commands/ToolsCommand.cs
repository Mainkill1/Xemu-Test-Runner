using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using XemuTestRunner.Config;
using XemuTestRunner.Diagnostics;

namespace XemuTestRunner.Commands;

public sealed class ToolsCommandSettings : CommandSettings
{
    [CommandOption("-c|--config <PATH>")]
    [Description("Path to runner JSON configuration.")]
    public string ConfigPath { get; set; } = "runner.json";
}

public sealed class ToolsCommand : Command<ToolsCommandSettings>
{
    protected override int Execute(
        CommandContext context,
        ToolsCommandSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            var (config, _) = ConfigLoader.Load(settings.ConfigPath);
            var table = new Table().RoundedBorder();
            table.AddColumn("Tool");
            table.AddColumn("Ready");
            table.AddColumn("Resolved");
            table.AddColumn("Detail");

            foreach (var tool in new DiagnosticToolCatalog(config.Diagnostics).ProbeAsync(cancellationToken).GetAwaiter().GetResult())
            {
                table.AddRow(
                    Markup.Escape(tool.Name),
                    tool.Available ? "[green]yes[/]" : "[yellow]no[/]",
                    Markup.Escape(tool.ResolvedPath ?? "-"),
                    Markup.Escape(tool.Detail));
            }

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine(
                "[grey]Tool absence only blocks recipes that require that tool. RenderDoc also requires its Python module and a compatible capture-capable xemu build.[/]");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
            return 1;
        }
    }
}
