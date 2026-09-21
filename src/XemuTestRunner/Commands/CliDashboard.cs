using Spectre.Console;
using XemuTestRunner.Runtime;

namespace XemuTestRunner.Commands;

internal static class CliDashboard
{
    public static Table Build(RunnerStateSnapshot s)
    {
        var table = new Table().RoundedBorder().BorderColor(Color.Grey).AddColumn("[bold]Xemu Test Runner[/]").AddColumn("[bold]Value[/]");
        void Row(string label, string? value) => table.AddRow(Markup.Escape(label), Markup.Escape(value ?? "-"));
        Row("Runner", s.Phase.ToUpperInvariant()); Row("Uptime", Duration(s.Uptime)); Row("HTTP", s.HttpEndpoint);
        Row("Queue", $"Pending {s.Queue.Pending} / Testing {s.Queue.Testing} / Tested {s.Queue.Tested}");
        Row("Current job", s.CurrentJob); Row("Run ID", s.RunId); Row("PID", s.ProcessId?.ToString());
        Row("Job runtime", s.JobStartedUtc is null ? "-" : Duration(DateTimeOffset.UtcNow - s.JobStartedUtc.Value));
        var m = s.LatestMetric;
        Row("Host CPU", Percent(m?.HostCpuPercent)); Row("Process CPU (core %)", Percent(m?.ProcessCpuPercent));
        Row("GPU", Percent(m?.GpuUtilizationPercent)); Row("Process GPU", Percent(m?.ProcessGpuUtilizationPercent));
        Row("Host memory", Bytes(m?.HostMemoryUsedBytes) + " / " + Bytes(m?.HostMemoryTotalBytes));
        Row("Process memory", Bytes(m?.ProcessWorkingSetBytes));
        Row("Swap / pagefile", m?.SwapTotalBytes is not null ? Bytes(m.SwapUsedBytes) + " / " + Bytes(m.SwapTotalBytes) : Percent(m?.PageFileUsagePercent));
        Row("VRAM", Bytes(m?.VramUsedBytes) + " / " + Bytes(m?.VramTotalBytes)); Row("Process VRAM", Bytes(m?.ProcessVramBytes));
        Row("Process I/O", $"R {Bytes(m?.ProcessReadBytesPerSecond)}/s / W {Bytes(m?.ProcessWriteBytesPerSecond)}/s");
        Row("GPU temp/power", $"{m?.GpuTemperatureC:0.0} C / {m?.GpuPowerWatts:0.0} W");
        Row("Collector", m is null ? "-" : $"{m.CollectorDurationMs:0.###} ms{(m.Overrun ? " OVERRUN" : "")}");
        Row("Sample age", m is null ? "-" : $"{Math.Max(0, (DateTimeOffset.UtcNow - m.TimestampUtc).TotalMilliseconds):0} ms");
        Row("Last job", s.LastJob); Row("Last result", s.LastResult);
        return table;
    }
    private static string Duration(TimeSpan value) => ((int)value.TotalHours).ToString("00") + ":" + value.Minutes.ToString("00") + ":" + value.Seconds.ToString("00");
    private static string Percent(double? value) => value is null ? "-" : $"{value:0.0}%";
    private static string Bytes(double? value)
    {
        if (value is null) return "-";
        var n = value.Value; var i = 0; string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        while (n >= 1024 && i < units.Length - 1) { n /= 1024; i++; }
        return $"{n:0.##} {units[i]}";
    }
}
