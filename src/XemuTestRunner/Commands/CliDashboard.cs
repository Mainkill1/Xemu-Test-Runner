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
            .AddColumn("[bold]Xemu Test Runner[/]")
            .AddColumn("[bold]Value[/]");

        void Row(string label, string? value) =>
            table.AddRow(
                Markup.Escape(label),
                Markup.Escape(value ?? "-"));

        Row("Runner", state.Phase.ToUpperInvariant());
        Row("HTTP", state.HttpEndpoint);
        Row(
            "Queue",
            $"Pending {state.Queue.Pending} / Testing {state.Queue.Testing} / Tested {state.Queue.Tested}");

        if (state.QueueIssue is not null)
        {
            Row(
                "Queue issue",
                $"{state.QueueIssue.Code}: {state.QueueIssue.Message}");
        }

        if (state.CurrentJob is not null)
        {
            var runtime = state.JobStartedUtc is null
                ? "-"
                : Duration(
                    DateTimeOffset.UtcNow -
                    state.JobStartedUtc.Value);

            Row(
                "Current test",
                $"{state.CurrentJob} / PID {state.ProcessId?.ToString() ?? "-"} / {runtime}");
        }

        var metric = state.LatestMetric;
        Row(
            "Host",
            $"CPU {Percent(metric?.HostCpuPercent)} / " +
            $"RAM {Bytes(metric?.HostMemoryUsedBytes)} / " +
            $"GPU {Percent(metric?.GpuUtilizationPercent)} / " +
            $"VRAM {Bytes(metric?.VramUsedBytes)}");

        if (state.CurrentJob is not null)
        {
            Row(
                "xemu",
                $"CPU {Percent(metric?.ProcessCpuPercent)} core / " +
                $"RAM {Bytes(metric?.ProcessWorkingSetBytes)} / " +
                $"GPU {Percent(metric?.ProcessGpuUtilizationPercent)} / " +
                $"VRAM {Bytes(metric?.ProcessVramBytes)}");

            Row(
                "xemu I/O",
                $"R {Rate(metric?.ProcessReadBytesPerSecond)} / " +
                $"W {Rate(metric?.ProcessWriteBytesPerSecond)}");
        }

        if (metric?.GpuTemperatureC is not null ||
            metric?.GpuPowerWatts is not null)
        {
            Row(
                "GPU sensors",
                $"{Number(metric?.GpuTemperatureC, "0.0", " C")} / " +
                $"{Number(metric?.GpuPowerWatts, "0.0", " W")}");
        }

        if (metric is not null)
        {
            Row(
                "Sampler",
                $"{metric.CollectorDurationMs:0.###} ms / " +
                $"{metric.CollectorDutyPercent:0.##}% duty" +
                (metric.Overrun ? " / OVERRUN" : ""));

            if (metric.Errors.Count > 0)
            {
                Row(
                    "Sampler errors",
                    string.Join(
                        " | ",
                        metric.Errors.Take(3)));
            }
        }

        if (state.LastJob is not null || state.LastResult is not null)
            Row("Last result", $"{state.LastJob ?? "-"} / {state.LastResult ?? "-"}");

        return table;
    }

    private static string Duration(TimeSpan value) =>
        ((int)value.TotalHours).ToString("00") +
        ":" + value.Minutes.ToString("00") +
        ":" + value.Seconds.ToString("00");

    private static string Percent(double? value) =>
        value is null ? "-" : $"{value:0.0}%";

    private static string Rate(double? bytesPerSecond) =>
        bytesPerSecond is null
            ? "-"
            : Bytes(bytesPerSecond) + "/s";

    private static string Bytes(double? value)
    {
        if (value is null)
            return "-";

        var number = value.Value;
        var unit = 0;
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];

        while (number >= 1024 && unit < units.Length - 1)
        {
            number /= 1024;
            unit++;
        }

        return $"{number:0.##} {units[unit]}";
    }

    private static string Number(
        double? value,
        string format,
        string suffix) =>
        value is null
            ? "-"
            : value.Value.ToString(format) + suffix;
}
