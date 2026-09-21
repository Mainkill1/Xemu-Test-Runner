using Spectre.Console.Cli;
using XemuTestRunner.Commands;

var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("xemu-test-runner");
    config.AddCommand<InitCommand>("init").WithDescription("Create a runner config and workspace folders.");
    config.AddCommand<RunCommand>("run").WithDescription("Run the foreground test queue and embedded LAN HTTP API.");
    config.AddCommand<QueueCommand>("queue").WithDescription("Show local queue state.");
    config.AddCommand<StatusCommand>("status").WithDescription("Query a running test runner over HTTP.");
    config.AddCommand<DoctorCommand>("doctor").WithDescription("Check platform, configuration, and telemetry provider availability.");
});

using var shutdown = new CancellationTokenSource();
ConsoleCancelEventHandler handler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};
Console.CancelKeyPress += handler;

try
{
    return await app.RunAsync(args, shutdown.Token);
}
finally
{
    Console.CancelKeyPress -= handler;
}
