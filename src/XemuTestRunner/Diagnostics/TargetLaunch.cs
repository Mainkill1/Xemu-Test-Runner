using System.Diagnostics;
using XemuTestRunner.Queue;

namespace XemuTestRunner.Diagnostics;

public sealed class TargetLaunch : IAsyncDisposable
{
    public Process Process { get; }
    public Stream? StandardOutput { get; }
    public Stream? StandardError { get; }
    public RenderDocSession? RenderDoc { get; }
    public string Mode { get; }

    private readonly bool _ownsProcess;

    private TargetLaunch(
        Process process,
        Stream? stdout,
        Stream? stderr,
        RenderDocSession? renderDoc,
        string mode,
        bool ownsProcess)
    {
        Process = process;
        StandardOutput = stdout;
        StandardError = stderr;
        RenderDoc = renderDoc;
        Mode = mode;
        _ownsProcess = ownsProcess;
    }

    public static async Task<TargetLaunch> StartAsync(
        DiagnosticsOptions diagnostics,
        JobDefinition job,
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        string resultDirectory,
        CancellationToken cancellationToken)
    {
        if (job.LaunchMode.Equals("renderdoc", StringComparison.OrdinalIgnoreCase))
        {
            if (!diagnostics.Enabled)
                throw new InvalidOperationException("RenderDoc launch requires Diagnostics.Enabled.");
            var session = await RenderDocSession.LaunchAsync(
                diagnostics,
                executable,
                workingDirectory,
                arguments,
                job.Environment,
                Path.Combine(resultDirectory, "diagnostics", "_renderdoc-session"),
                cancellationToken).ConfigureAwait(false);
            return new TargetLaunch(
                session.TargetProcess,
                null,
                null,
                session,
                "renderdoc",
                ownsProcess: false);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = false
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        foreach (var variable in job.Environment)
            startInfo.Environment[variable.Key] = variable.Value;

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Process.Start returned false.");
        }

        return new TargetLaunch(
            process,
            process.StandardOutput.BaseStream,
            process.StandardError.BaseStream,
            null,
            "direct",
            ownsProcess: true);
    }

    public async ValueTask DisposeAsync()
    {
        if (RenderDoc is not null)
            await RenderDoc.DisposeAsync().ConfigureAwait(false);
        if (_ownsProcess)
            Process.Dispose();
    }
}
