using Spectre.Console;
using XemuTestRunner.Runtime;

namespace XemuTestRunner.Commands;

internal static class CliDashboard
{
    public static Table Build(RunnerStateSnapshot state)
    {
        var table = new Table()
            .RoundedBorder()
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]Xemu Test Runner[/]").LeftAligned())
            .AddColumn(new TableColumn("[bold]Value[/]").LeftAligned());

        table.AddRow("Runner", PhaseMarkup(state.Phase));
        table.AddRow("Uptime", FormatDuration(state.Uptime));
        table.AddRow("HTTP", Escape(state.HttpEndpoint ?? "disabled"));
        table.AddRow("", "");

        table.AddRow("Queue", $"Pending [bold]{state.Queue.Pending}[/]   Testing [bold]{state.Queue.Testing}[/]   Tested [bold]{state.Queue.Tested}[/]");
        table.AddRow("Current job", Escape(state.CurrentJob ?? "-"));
        table.AddRow("Run ID", Escape(state.RunId ?? "-"));
        table.AddRow("PID", state.ProcessId?.ToString() ?? "-");
        table.AddRow(
            "Job runtime",
            state.JobStartedUtc is null
                ? "-"
                : FormatDuration(DateTimeOffset.UtcNow - state.JobStartedUtc.Value));

        table.AddRow("", "");

        var metric = state.LatestMetric;
        table.AddRow("Host CPU", Percent(metric?.HostCpuPercent));
        table.AddRow("Process CPU", CorePercent(metric?.ProcessCpuPercent));
        table.AddRow("GPU", Percent(metric?.GpuUtilizationPercent));
        table.AddRow("Process GPU", Percent(metric?.ProcessGpuUtilizationPercent));
        table.AddRow(
            "Host memory",
            metric?.HostMemoryUsedBytes is null
                ? "-"
                : $"{Bytes(metric.HostMemoryUsedBytes)} / {Bytes(metric.HostMemoryTotalBytes)}");
        table.AddRow("Process memory", Bytes(metric?.ProcessWorkingSetBytes));
        table.AddRow(
            "Swap / pagefile",
            metric is null
                ? "-"
                : metric.SwapTotalBytes is not null
                    ? $"{Bytes(metric.SwapUsedBytes)} / {Bytes(metric.SwapTotalBytes)}"
                    : metric.PageFileUsagePercent is not null
                        ? $"{metric.PageFileUsagePercent:0.0}%"
                        : "-");
        table.AddRow(
            "VRAM",
            metric?.VramUsedBytes is null
                ? "-"
                : $"{Bytes(metric.VramUsedBytes)} / {Bytes(metric.VramTotalBytes)}");
        table.AddRow("Process VRAM", Bytes(metric?.ProcessVramBytes));
        table.AddRow(
            "Process I/O",
            metric is null
                ? "-"
                : $"R {Rate(metric.ProcessReadBytesPerSecond)}   W {Rate(metric.ProcessWriteBytesPerSecond)}");
        table.AddRow(
            "GPU temp / power",
            metric is null
                ? "-"
                : $"{Number(metric.GpuTemperatureC, "0.0", " C")}   {Number(metric.GpuPowerWatts, "0.0", " W")}");

        table.AddRow("", "");
        table.AddRow(
            "Collector",
            metric is null
                ? "-"
                : $"{metric.CollectorDurationMs:0.###} ms{(metric.Overrun ? " [red]OVERRUN[/]" : "")}");
        table.AddRow("Last job", Escape(state.LastJob ?? "-"));
        table.AddRow("Last result", ResultMarkup(state.LastResult));

        return table;
    }

    private static string PhaseMarkup(string phase) => phase.ToLowerInvariant() switch
    {
        "running" => "[green]RUNNING[/]",
        "paused" => "[yellow]PAUSED[/]",
        "idle" => "[deepskyblue1]IDLE[/]",
        "finished" => "[green]FINISHED[/]",
        "interrupted" => "[red]INTERRUPTED[/]",
        "stopped" => "[grey]STOPPED[/]",
        "starting" => "[grey]STARTING[/]",
        _ => Escape(phase.ToUpperInvariant())
    };

    private static string ResultMarkup(string? result) => result?.ToLowerInvariant() switch
    {
        null => "-",
        "completed" => "[green]completed[/]",
        "failed" => "[red]failed[/]",
        "timeout" => "[yellow]timeout[/]",
        "cancelled" => "[yellow]cancelled[/]",
        "plan_failed" => "[red]plan_failed[/]",
        "runner_error" => "[red]runner_error[/]",
        "invalid_job" => "[red]invalid_job[/]",
        "start_failed" => "[red]start_failed[/]",
        _ => Escape(result)
    };

    private static string Percent(double? value) => value is null ? "-" : $"{value.Value:0.0}%";
    private static string CorePercent(double? value) => value is null ? "-" : $"{value.Value:0.0}% core";
    private static string Rate(double? bytesPerSecond) => bytesPerSecond is null ? "-" : $"{Bytes((long)bytesPerSecond.Value)}/s";

    private static string Bytes(long? bytes)
    {
        if (bytes is null)
            return "-";

        double value = bytes.Value;
        string[] suffixes = ["B", "KiB", "MiB", "GiB", "TiB"];
        var suffix = 0;

        while (value >= 1024 && suffix < suffixes.Length - 1)
        {
            value /= 1024;
            suffix++;
        }

        return $"{value:0.##} {suffixes[suffix]}";
    }

    private static string Number(double? value, string format, string suffix) =>
        value is null ? "-" : value.Value.ToString(format) + suffix;

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
            return $"{(int)duration.TotalDays}d {duration:hh\:mm\:ss}";

        return duration.ToString(@"hh\:mm\:ss");
    }

    private static string Escape(string value) => Markup.Escape(value);
}
