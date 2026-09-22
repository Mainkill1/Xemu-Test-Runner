using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Text.Json;

namespace XemuTestRunner.Commands;

public sealed class StatusCommandSettings : CommandSettings
{
    [CommandOption("-u|--url <URL>")]
    [Description("Runner base URL.")]
    public string Url { get; set; } = "http://127.0.0.1:9368";

    [CommandOption("--json")]
    [Description("Emit the runner status JSON without a Spectre table.")]
    public bool Json { get; set; }
}

public sealed class StatusCommand : Command<StatusCommandSettings>
{
    protected override int Execute(CommandContext context, StatusCommandSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient
            {
                BaseAddress = new Uri(settings.Url.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(3)
            };
            var json = client.GetStringAsync("api/v1/status", cancellationToken).GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(json);
            if (settings.Json)
            {
                Console.WriteLine(
                    JsonSerializer.Serialize(
                        document.RootElement));
                return 0;
            }

            var root = document.RootElement;
            var table = new Table().RoundedBorder();
            table.AddColumn("Field");
            table.AddColumn("Value");
            Add(table, "Phase", root, "Phase");
            Add(table, "Current job", root, "CurrentJob");
            Add(table, "Run ID", root, "RunId");
            Add(table, "Process ID", root, "ProcessId");

            if (root.TryGetProperty("Queue", out var queue))
            {
                Add(table, "Pending", queue, "Pending");
                Add(table, "Testing", queue, "Testing");
                Add(table, "Tested", queue, "Tested");
            }

            if (root.TryGetProperty("LatestMetric", out var metric) && metric.ValueKind == JsonValueKind.Object)
            {
                Add(table, "Host CPU %", metric, "HostCpuPercent");
                Add(table, "Process CPU core %", metric, "ProcessCpuPercent");
                Add(table, "GPU %", metric, "GpuUtilizationPercent");
                Add(table, "Process GPU %", metric, "ProcessGpuUtilizationPercent");
            }

            AnsiConsole.Write(table);
            return 0;
        }
        catch (Exception ex)
        {
            var advice =
                UserErrorAdviceFactory.From(
                    ex,
                    "Runner status request failed");

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
                        }));
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

    private static void Add(Table table, string label, JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
            return;
        table.AddRow(label, Markup.Escape(value.ToString()));
    }
}
