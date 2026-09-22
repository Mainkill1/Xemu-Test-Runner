using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using XemuTestRunner.Config;
using XemuTestRunner.Runtime;

namespace XemuTestRunner.Commands;

public sealed class RunCommandSettings : CommandSettings
{
    [CommandOption("-c|--config <PATH>")]
    [Description("Path to runner JSON configuration.")]
    public string ConfigPath { get; set; } = "runner.json";

    [CommandOption("--once")]
    [Description("Process the current queue and exit when it becomes empty.")]
    public bool Once { get; set; }

    [CommandOption("--one-shot")]
    [Description("Process at most one claimed package and exit.")]
    public bool OneShot { get; set; }

    [CommandOption("--non-interactive")]
    [Description("Disable Spectre live rendering and emit plain state changes.")]
    public bool NonInteractive { get; set; }

    [CommandOption("--json")]
    [Description("Emit exactly one machine-readable JSON summary on stdout. Implies --non-interactive.")]
    public bool Json { get; set; }
}

public sealed class RunCommand : Command<RunCommandSettings>
{
    private static readonly JsonSerializerOptions AutomationJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    protected override int Execute(
        CommandContext context,
        RunCommandSettings settings,
        CancellationToken cancellationToken)
    {
        RunnerEngine? engine = null;
        var startedUtc = DateTimeOffset.UtcNow;

        try
        {
            var (config, paths) =
                ConfigLoader.Load(settings.ConfigPath);

            engine = new RunnerEngine(config, paths);

            var finiteRun =
                settings.Once ||
                settings.OneShot;

            var maxJobs =
                settings.OneShot
                    ? 1
                    : (int?)null;

            var nonInteractive =
                settings.NonInteractive ||
                settings.Json ||
                Console.IsOutputRedirected;

            var runTask = engine.RunAsync(
                finiteRun,
                maxJobs,
                cancellationToken);

            if (settings.Json)
            {
                runTask.GetAwaiter().GetResult();
            }
            else if (nonInteractive)
            {
                MonitorPlainAsync(
                    engine,
                    runTask,
                    config.Ui.CliRefreshMs,
                    cancellationToken)
                    .GetAwaiter()
                    .GetResult();

                runTask.GetAwaiter().GetResult();
            }
            else
            {
                WaitForEngineAfterTerminalFailureAsync(
                    () => RunLiveDashboardAsync(
                        engine,
                        runTask,
                        config.Ui.CliRefreshMs,
                        cancellationToken),
                    runTask)
                    .GetAwaiter()
                    .GetResult();
            }

            var snapshot = engine.State.Snapshot();
            var exitCode = DetermineExitCode(
                snapshot,
                cancelled:
                    cancellationToken.IsCancellationRequested);

            WriteFinalOutput(
                settings,
                snapshot,
                startedUtc,
                DateTimeOffset.UtcNow,
                exitCode,
                error: null,
                errorCode: null,
                errorHint: null);

            return exitCode;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            var snapshot =
                engine?.State.Snapshot();

            const int exitCode = 130;

            WriteFinalOutput(
                settings,
                snapshot,
                startedUtc,
                DateTimeOffset.UtcNow,
                exitCode,
                error: null,
                errorCode: null,
                errorHint: null,
                cancelled: true);

            return exitCode;
        }
        catch (Exception ex)
        {
            const int exitCode = 1;
            var advice =
                UserErrorAdviceFactory.From(
                    ex,
                    "Runner failed");

            if (settings.Json)
            {
                WriteFinalOutput(
                    settings,
                    engine?.State.Snapshot(),
                    startedUtc,
                    DateTimeOffset.UtcNow,
                    exitCode,
                    advice.Error,
                    advice.Code,
                    advice.Hint);
            }
            else if (
                settings.NonInteractive ||
                Console.IsOutputRedirected)
            {
                Console.Error.WriteLine(
                    $"{advice.Code}: {advice.Error}");
                Console.Error.WriteLine(
                    "hint: " + advice.Hint);
            }
            else
            {
                ReportInteractiveFailure(
                    ex,
                    error =>
                    {
                        if (advice.Code == "internal_error")
                        {
                            AnsiConsole.WriteException(
                                error,
                                ExceptionFormats.ShortenEverything);
                        }
                        else
                        {
                            AnsiConsole.MarkupLine(
                                $"[red]{Markup.Escape(advice.Code)}:[/] {Markup.Escape(advice.Error)}");
                        }

                        AnsiConsole.MarkupLine(
                            $"[yellow]Hint:[/] {Markup.Escape(advice.Hint)}");
                    },
                    error =>
                    {
                        Console.Error.WriteLine(
                            advice.Code == "internal_error"
                                ? error
                                : $"{advice.Code}: {advice.Error}");
                        Console.Error.WriteLine(
                            "hint: " + advice.Hint);
                    });
            }

            return exitCode;
        }
    }

    public static int DetermineExitCode(
        RunnerStateSnapshot snapshot,
        bool cancelled)
    {
        if (cancelled)
            return 130;

        if (snapshot.QueueIssue is not null ||
            snapshot.Phase.Equals(
                "queue_blocked",
                StringComparison.OrdinalIgnoreCase) ||
            snapshot.Phase.Equals(
                "interrupted",
                StringComparison.OrdinalIgnoreCase) ||
            snapshot.Phase.Equals(
                "waiting_for_package",
                StringComparison.OrdinalIgnoreCase))
            return 3;

        if (snapshot.FailedJobs > 0)
            return 2;

        if (snapshot.Phase.Equals(
                "faulted",
                StringComparison.OrdinalIgnoreCase))
            return 1;

        return 0;
    }

    public static async Task WaitForEngineAfterTerminalFailureAsync(
        Func<Task> liveDisplay,
        Task engineTask)
    {
        try
        {
            await liveDisplay().ConfigureAwait(false);
        }
        catch (Exception ex)
            when (ex is IOException or InvalidOperationException or NotSupportedException)
        {
        }

        await engineTask.ConfigureAwait(false);
    }

    public static void ReportInteractiveFailure(
        Exception error,
        Action<Exception> richWriter,
        Action<Exception> fallbackWriter)
    {
        try
        {
            richWriter(error);
        }
        catch (Exception ex)
            when (ex is IOException or InvalidOperationException or NotSupportedException)
        {
            try
            {
                fallbackWriter(error);
            }
            catch (Exception fallbackError)
                when (fallbackError is IOException or InvalidOperationException or NotSupportedException)
            {
            }
        }
    }

    private static Task RunLiveDashboardAsync(
        RunnerEngine engine,
        Task runTask,
        int refreshMs,
        CancellationToken cancellationToken)
    {
        return AnsiConsole.Live(
                CliDashboard.Build(
                    engine.State.Snapshot()))
            .AutoClear(false)
            .StartAsync(async live =>
            {
                try
                {
                    while (!runTask.IsCompleted)
                    {
                        live.UpdateTarget(
                            CliDashboard.Build(
                                engine.State.Snapshot()));
                        live.Refresh();

                        await Task.Delay(
                                refreshMs,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                }
                finally
                {
                    live.UpdateTarget(
                        CliDashboard.Build(
                            engine.State.Snapshot()));
                    live.Refresh();
                }
            });
    }

    private static async Task MonitorPlainAsync(
        RunnerEngine engine,
        Task runTask,
        int refreshMs,
        CancellationToken cancellationToken)
    {
        string? lastSignature = null;

        while (!runTask.IsCompleted)
        {
            var snapshot =
                engine.State.Snapshot();
            var signature =
                ProgressSignature(snapshot);

            if (!string.Equals(
                    lastSignature,
                    signature,
                    StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    PlainProgress(snapshot));
                lastSignature = signature;
            }

            var delay = Task.Delay(
                Math.Max(100, refreshMs),
                cancellationToken);

            await Task.WhenAny(
                    runTask,
                    delay)
                .ConfigureAwait(false);
        }

        var final = engine.State.Snapshot();
        var finalSignature =
            ProgressSignature(final);

        if (!string.Equals(
                lastSignature,
                finalSignature,
                StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                PlainProgress(final));
        }
    }

    private static string ProgressSignature(
        RunnerStateSnapshot snapshot) =>
        string.Join(
            "|",
            snapshot.Phase,
            snapshot.CurrentJob ?? "",
            snapshot.RunId ?? "",
            snapshot.Queue.Pending,
            snapshot.Queue.Testing,
            snapshot.Queue.Tested,
            snapshot.QueueIssue?.Code ?? "",
            snapshot.LastResult ?? "",
            snapshot.JobsFinished,
            snapshot.FailedJobs);

    private static string PlainProgress(
        RunnerStateSnapshot snapshot)
    {
        var parts = new List<string>
        {
            DateTimeOffset.UtcNow.ToString("O"),
            "phase=" + snapshot.Phase,
            $"queue={snapshot.Queue.Pending}/{snapshot.Queue.Testing}/{snapshot.Queue.Tested}"
        };

        if (snapshot.CurrentJob is not null)
            parts.Add("job=" + snapshot.CurrentJob);

        if (snapshot.ProcessId is not null)
            parts.Add("pid=" + snapshot.ProcessId);

        if (snapshot.QueueIssue is not null)
            parts.Add(
                "queue_issue=" +
                snapshot.QueueIssue.Code);

        if (snapshot.LastResult is not null)
            parts.Add(
                "last_result=" +
                snapshot.LastResult);

        return string.Join(" ", parts);
    }

    private static void WriteFinalOutput(
        RunCommandSettings settings,
        RunnerStateSnapshot? snapshot,
        DateTimeOffset startedUtc,
        DateTimeOffset endedUtc,
        int exitCode,
        string? error,
        string? errorCode,
        string? errorHint,
        bool cancelled = false)
    {
        var evidenceDirectory =
            snapshot?.LastResultDirectory;

        var resultFile =
            evidenceDirectory is null
                ? null
                : Path.Combine(
                    evidenceDirectory,
                    "result.json");

        var summary = new AutomationRunSummary(
            Ok: exitCode == 0,
            ExitCode: exitCode,
            Cancelled: cancelled,
            Mode:
                settings.OneShot
                    ? "one-shot"
                    : settings.Once
                        ? "once"
                        : "continuous",
            StartedUtc: startedUtc,
            EndedUtc: endedUtc,
            DurationMs:
                (endedUtc - startedUtc)
                    .TotalMilliseconds,
            Phase: snapshot?.Phase,
            JobsFinished:
                snapshot?.JobsFinished ?? 0,
            FailedJobs:
                snapshot?.FailedJobs ?? 0,
            LastJob:
                snapshot?.LastJob,
            LastResult:
                snapshot?.LastResult,
            EvidenceDirectory:
                evidenceDirectory,
            ResultFile:
                resultFile,
            Queue:
                snapshot?.Queue,
            QueueIssue:
                snapshot?.QueueIssue,
            HttpEndpoint:
                snapshot?.HttpEndpoint,
            Error: error,
            ErrorCode: errorCode,
            ErrorHint: errorHint);

        if (settings.Json)
        {
            Console.Out.WriteLine(
                JsonSerializer.Serialize(
                    summary,
                    AutomationJson));
            return;
        }

        if (settings.NonInteractive ||
            Console.IsOutputRedirected)
        {
            Console.Out.WriteLine(
                $"final exit_code={summary.ExitCode} " +
                $"ok={summary.Ok.ToString().ToLowerInvariant()} " +
                $"jobs_finished={summary.JobsFinished} " +
                $"failed_jobs={summary.FailedJobs} " +
                $"last_result={summary.LastResult ?? "-"} " +
                $"result_file={summary.ResultFile ?? "-"}");
        }
    }
}

public sealed record AutomationRunSummary(
    bool Ok,
    int ExitCode,
    bool Cancelled,
    string Mode,
    DateTimeOffset StartedUtc,
    DateTimeOffset EndedUtc,
    double DurationMs,
    string? Phase,
    int JobsFinished,
    int FailedJobs,
    string? LastJob,
    string? LastResult,
    string? EvidenceDirectory,
    string? ResultFile,
    XemuTestRunner.Queue.QueueSnapshot? Queue,
    XemuTestRunner.Queue.QueueIssue? QueueIssue,
    string? HttpEndpoint,
    string? Error,
    string? ErrorCode,
    string? ErrorHint);
