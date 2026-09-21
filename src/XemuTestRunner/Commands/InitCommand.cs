using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using XemuTestRunner.Config;
using XemuTestRunner.Queue;

namespace XemuTestRunner.Commands;

public sealed class InitCommandSettings : CommandSettings
{
    [CommandOption("-c|--config <PATH>")]
    [Description("Config file to create.")]
    public string ConfigPath { get; set; } = "runner.json";

    [CommandOption("--force")]
    [Description("Overwrite an existing config file.")]
    public bool Force { get; set; }
}

public sealed class InitCommand : Command<InitCommandSettings>
{
    protected override int Execute(CommandContext context, InitCommandSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            ConfigLoader.WriteExample(settings.ConfigPath, settings.Force);
            var (config, paths) = ConfigLoader.Load(settings.ConfigPath);
            new JobQueue(config, paths).EnsureDirectories();

            var table = new Table().RoundedBorder();
            table.AddColumn("Item");
            table.AddColumn("Path");
            table.AddRow("Config", Markup.Escape(paths.ConfigFile));
            table.AddRow("Workspace", Markup.Escape(paths.Workspace));
            table.AddRow("Pending", Markup.Escape(paths.Pending));
            table.AddRow("Testing", Markup.Escape(paths.Testing));
            table.AddRow("Tested", Markup.Escape(paths.Tested));
            table.AddRow("Results", Markup.Escape(paths.Results));
            table.AddRow("File root", Markup.Escape(paths.FileRoot));
            AnsiConsole.Write(table);
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex, ExceptionFormats.ShortenEverything);
            return 1;
        }
    }
}
