using System.ComponentModel;
using System.Text.Json;
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

    [CommandOption("--json")]
    [Description("Emit machine-readable validation output.")]
    public bool Json { get; set; }
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
            if (settings.Json)
            {
                Console.Out.WriteLine(
                    JsonSerializer.Serialize(
                        new
                        {
                            ok = report.Passed,
                            code = report.Passed
                                ? "package_valid"
                                : "preflight_failed",
                            package,
                            executableSha256 =
                                report.ExecutableSha256,
                            checks = report.Checks,
                            hint = report.Passed
                                ? "Package preflight passed."
                                : "Correct each failed check before queueing the package."
                        },
                        ConfigLoader.JsonOptions));
                return report.Passed ? 0 : 2;
            }

            var table = new Table()
                .RoundedBorder()
                .AddColumn("Check")
                .AddColumn("Result")
                .AddColumn("Detail");

            foreach (var check in report.Checks)
            {
                table.AddRow(
                    Markup.Escape(check.Name),
                    check.Passed
                        ? "[green]PASS[/]"
                        : "[red]FAIL[/]",
                    Markup.Escape(check.Detail));
            }

            AnsiConsole.Write(table);

            if (!report.Passed)
                AnsiConsole.MarkupLine(
                    "[yellow]Hint:[/] Correct the failed package/preflight checks before moving the package into Pending.");

            return report.Passed ? 0 : 2;
        }
        catch (Exception e)
            when (e is not OperationCanceledException)
        {
            var advice =
                UserErrorAdviceFactory.From(
                    e,
                    "Package validation failed");

            if (settings.Json)
            {
                Console.Out.WriteLine(
                    JsonSerializer.Serialize(
                        new
                        {
                            ok = false,
                            advice.Code,
                            advice.Error,
                            advice.Hint
                        },
                        ConfigLoader.JsonOptions));
            }
            else
            {
                AnsiConsole.MarkupLine(
                    $"[red]{Markup.Escape(advice.Code)}:[/] {Markup.Escape(advice.Error)}");
                AnsiConsole.MarkupLine(
                    $"[yellow]Hint:[/] {Markup.Escape(advice.Hint)}");
            }

            return 2;
        }
    }
}
