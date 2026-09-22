using Spectre.Console.Cli;
using XemuTestRunner.Commands;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("xemu-test-runner");
    config.SetApplicationVersion(XemuTestRunner.ApplicationInfo.DisplayVersion);
    config.AddCommand<InitCommand>("init").WithDescription("Create config and workspace folders.");
    config.AddCommand<RunCommand>("run").WithDescription("Run the foreground queue, dashboard, and embedded HTTP.");
    config.AddCommand<QueueCommand>("queue").WithDescription("Show local queue state.");
    config.AddCommand<StatusCommand>("status").WithDescription("Query a running runner over HTTP.");
    config.AddCommand<DoctorCommand>("doctor").WithDescription("Check platform and provider availability.");
    config.AddCommand<ToolsCommand>("tools").WithDescription("Show deep diagnostic tool readiness.");
    config.AddCommand<ValidateCommand>("validate").WithDescription("Check a packaged build before launching it.");
    config.AddCommand<CompareCommand>("compare").WithDescription("Aggregate eligible measurements for an experiment.");
});
using var shutdown = new CancellationTokenSource();
ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; shutdown.Cancel(); };
Console.CancelKeyPress += handler;
try { return await app.RunAsync(args, shutdown.Token); }
finally { Console.CancelKeyPress -= handler; }
