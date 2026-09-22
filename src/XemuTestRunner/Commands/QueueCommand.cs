using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Queue;

namespace XemuTestRunner.Commands;

public sealed class QueueCommandSettings : CommandSettings
{
    [CommandOption("-c|--config <PATH>")]
    [Description("Path to runner JSON configuration.")]
    public string ConfigPath { get; set; } = "runner.json";

    [CommandOption("--json")]
    [Description("Emit machine-readable JSON instead of a Spectre table.")]
    public bool Json { get; set; }
}

public sealed class QueueCommand : Command<QueueCommandSettings>
{
    protected override int Execute(CommandContext context, QueueCommandSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var (config, paths) = ConfigLoader.Load(settings.ConfigPath);
            var queue = new JobQueue(config, paths);
            queue.EnsureDirectories();
            var snapshot = queue.Snapshot();
            var interrupted = queue.GetTestingJobs();

            if (settings.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    queue = snapshot,
                    testingPackages = interrupted
                        .Select(Path.GetFileName)
                        .ToArray()
                }, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }));
                return 0;
            }

            var table = new Table().RoundedBorder();
            table.AddColumn("State");
            table.AddColumn(new TableColumn("Jobs").RightAligned());
            table.AddRow("Pending", snapshot.Pending.ToString());
            table.AddRow("Testing", snapshot.Testing.ToString());
            table.AddRow("Tested", snapshot.Tested.ToString());
            AnsiConsole.Write(table);

            if (interrupted.Count > 0)
                AnsiConsole.MarkupLine($"[yellow]Testing contains:[/] {Markup.Escape(string.Join(", ", interrupted.Select(Path.GetFileName)))}");
            return 0;
        }
        catch (Exception ex)
        {
            var advice =
                UserErrorAdviceFactory.From(
                    ex,
                    "Queue inspection failed");

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

            return 1;
        }
    }
}
