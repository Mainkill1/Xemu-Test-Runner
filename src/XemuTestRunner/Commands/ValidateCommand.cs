using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using XemuTestRunner.Config;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Commands;

public sealed class ValidateSettings : CommandSettings
{
    [CommandArgument(0, "<PACKAGE>")]
    [Description("Directory containing job.json and the candidate build.")]
    public string Package { get; set; } = "";
    [CommandOption("-c|--config <PATH>")]
    public string ConfigPath { get; set; } = "runner.json";
}
public sealed class ValidateCommand : Command<ValidateSettings>
{
    protected override int Execute(CommandContext context, ValidateSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var (config, paths) = ConfigLoader.Load(settings.ConfigPath);
            var package = Path.GetFullPath(settings.Package);
            var job = JobDefinition.LoadPackage(package);
            var report = Preflight.CheckAsync(job, package, paths.Results, config.Reliability.Preflight, cancellationToken).GetAwaiter().GetResult();
            var table = new Table().RoundedBorder().AddColumn("Check").AddColumn("Result").AddColumn("Detail");
            foreach (var check in report.Checks)
                table.AddRow(Markup.Escape(check.Name), check.Passed ? "[green]PASS[/]" : "[red]FAIL[/]", Markup.Escape(check.Detail));
            AnsiConsole.Write(table);
            return report.Passed ? 0 : 2;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        { AnsiConsole.MarkupLine("[red]Preflight failed:[/] " + Markup.Escape(e.Message)); return 2; }
    }
}
