using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Runtime.InteropServices;
using XemuTestRunner.Config;
using XemuTestRunner.Monitoring.Providers;

namespace XemuTestRunner.Commands;

public sealed class DoctorCommandSettings : CommandSettings
{
    [CommandOption("-c|--config <PATH>")]
    [Description("Path to runner JSON configuration.")]
    public string ConfigPath { get; set; } = "runner.json";
}

public sealed class DoctorCommand : Command<DoctorCommandSettings>
{
    protected override int Execute(CommandContext context, DoctorCommandSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var (config, paths) = ConfigLoader.Load(settings.ConfigPath);
            var table = new Table().RoundedBorder();
            table.AddColumn("Check");
            table.AddColumn("Value");
            table.AddRow("OS", Markup.Escape(RuntimeInformation.OSDescription));
            table.AddRow("Architecture", RuntimeInformation.ProcessArchitecture.ToString());
            table.AddRow(".NET", Environment.Version.ToString());
            table.AddRow("CPU logical processors", Environment.ProcessorCount.ToString());
            table.AddRow("Workspace", Markup.Escape(paths.Workspace));
            table.AddRow("Sample interval", $"{config.Monitoring.IntervalMs} ms");
            table.AddRow("HTTP", config.Http.Enabled ? $"{Markup.Escape(config.Http.BindAddress)}:{config.Http.Port}" : "disabled");
            table.AddRow("xemu control", config.XemuControl.Enabled ? "enabled" : "disabled");

            if (config.XemuControl.Enabled)
            {
                var qmpPort = config.XemuControl.QmpPort == 0 ? "automatic" : config.XemuControl.QmpPort.ToString();
                table.AddRow("QMP", $"{Markup.Escape(config.XemuControl.QmpHost)}:{qmpPort}");

                var inputStatus = OperatingSystem.IsWindows()
                    ? "Windows SendInput; active xemu window required"
                    : OperatingSystem.IsLinux()
                        ? string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"))
                            ? "Linux X11/XTest unavailable: DISPLAY is not set"
                            : "Linux X11/XTest; runtime library/window check occurs when xemu starts"
                        : "No input provider for this OS";

                table.AddRow("Controller input", Markup.Escape(inputStatus));
            }

            if (config.Monitoring.Gpu.Enabled)
            {
                NvidiaNvmlProvider.TryCreate(config.Monitoring.Gpu, out var nvml, out var nvmlStatus);
                table.AddRow("NVIDIA NVML", Markup.Escape(nvmlStatus));
                nvml?.Dispose();

                WindowsGpuPerformanceCounterProvider.TryCreate(config.Monitoring.Gpu, out var windows, out var windowsStatus);
                table.AddRow("Windows GPU counters", Markup.Escape(windowsStatus));
                windows?.Dispose();

                LinuxDrmProvider.TryCreate(config.Monitoring.Gpu, out var drm, out var drmStatus);
                table.AddRow("Linux DRM", Markup.Escape(drmStatus));
                drm?.Dispose();
            }

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
