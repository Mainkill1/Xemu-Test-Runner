using System.ComponentModel;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using XemuTestRunner.Analysis;
using XemuTestRunner.Config;

namespace XemuTestRunner.Commands;

public sealed class CompareCommandSettings :
    CommandSettings
{
    [CommandOption("-c|--config <PATH>")]
    public string ConfigPath { get; set; } =
        "runner.json";

    [CommandOption("-e|--experiment <ID>")]
    [Description("Experiment ID to aggregate.")]
    public string ExperimentId { get; set; } =
        "";

    [CommandOption("--results <PATH>")]
    [Description("Override the configured results directory.")]
    public string? ResultsPath { get; set; }

    [CommandOption("--json")]
    public bool Json { get; set; }
}

public sealed class CompareCommand :
    Command<CompareCommandSettings>
{
    protected override int Execute(
        CommandContext context,
        CompareCommandSettings settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(
                settings.ExperimentId))
        {
            AnsiConsole.MarkupLine(
                "[red]--experiment is required.[/]");
            return 2;
        }

        try
        {
            var (_, paths) =
                ConfigLoader.Load(
                    settings.ConfigPath);
            var results = string.IsNullOrWhiteSpace(
                settings.ResultsPath)
                ? paths.Results
                : Path.GetFullPath(
                    settings.ResultsPath);

            var comparison =
                ComparisonAnalyzer.Analyze(
                    results,
                    settings.ExperimentId);

            if (settings.Json)
            {
                Console.Out.WriteLine(
                    JsonSerializer.Serialize(
                        comparison,
                        ConfigLoader.JsonOptions));
                return comparison.EligibleAttempts > 0
                    ? 0
                    : 2;
            }

            AnsiConsole.MarkupLine(
                $"[bold]Experiment:[/] {Markup.Escape(comparison.ExperimentId)}  " +
                $"[bold]Attempts:[/] {comparison.TotalAttempts}  " +
                $"[bold]Eligible:[/] {comparison.EligibleAttempts}");

            var table = new Table()
                .RoundedBorder()
                .AddColumn("Metric")
                .AddColumn("Variant")
                .AddColumn("N")
                .AddColumn("Mean")
                .AddColumn("Min")
                .AddColumn("Max")
                .AddColumn("StdDev")
                .AddColumn("Direction");

            foreach (var item in
                     comparison.Measurements)
            {
                string Format(double value) =>
                    string.IsNullOrWhiteSpace(
                        item.Unit)
                        ? $"{value:0.###}"
                        : $"{value:0.###} {item.Unit}";

                table.AddRow(
                    Markup.Escape(item.Name),
                    Markup.Escape(item.Variant),
                    item.Count.ToString(),
                    Markup.Escape(
                        Format(item.Mean)),
                    Markup.Escape(
                        Format(item.Minimum)),
                    Markup.Escape(
                        Format(item.Maximum)),
                    Markup.Escape(
                        Format(item.StandardDeviation)),
                    Markup.Escape(item.Direction));
            }

            AnsiConsole.Write(table);

            if (comparison.Ineligible.Count > 0)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]{comparison.Ineligible.Count} attempt(s) excluded by comparison eligibility.[/]");

                foreach (var attempt in
                         comparison.Ineligible.Take(10))
                {
                    AnsiConsole.MarkupLine(
                        $"[grey]- {Markup.Escape(attempt.RunId)} / {Markup.Escape(attempt.Variant)}: " +
                        $"{Markup.Escape(string.Join("; ", attempt.Reasons))}[/]");
                }
            }

            return comparison.EligibleAttempts > 0
                ? 0
                : 2;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(
                ex,
                ExceptionFormats.ShortenEverything);
            return 1;
        }
    }
}
