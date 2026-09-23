using System.Diagnostics;
using XemuTestRunner.Queue;
using XemuTestRunner.Runtime;

namespace XemuTestRunner.Diagnostics;

public sealed class TargetLaunch : IAsyncDisposable
{
    public Process Process { get; }
    public Stream? StandardOutput { get; }
    public Stream? StandardError { get; }
    public RenderDocSession? RenderDoc { get; }
    public string Mode { get; }
    private readonly bool _ownsProcess;
    private PosixTarget? _posix;
    private RunStorageSession? _storage;
    public int ExitCode => (_posix?.Supervisor ?? Process).ExitCode;
    public NativeExitStatus? NativeExit => _posix?.ReadExit();
    public Task WaitForExitAsync() => (_posix?.Supervisor ?? Process).WaitForExitAsync();

    private TargetLaunch(Process process, Stream? stdout, Stream? stderr,
        RenderDocSession? renderDoc, string mode, bool ownsProcess)
    {
        Process = process; StandardOutput = stdout; StandardError = stderr;
        RenderDoc = renderDoc; Mode = mode; _ownsProcess = ownsProcess;
    }

    public static async Task<TargetLaunch> StartAsync(
        DiagnosticsOptions diagnostics, JobDefinition job, string executable, string workingDirectory,
        IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment,
        string resultDirectory, CancellationToken cancellationToken)
    {
        var storage = await RunStorageSession.PrepareAsync(job, executable, workingDirectory,
            arguments, environment, resultDirectory, cancellationToken).ConfigureAwait(false);
        try
        {
            if (job.LaunchMode.Equals("renderdoc", StringComparison.OrdinalIgnoreCase))
            {
                if (!diagnostics.Enabled) throw new InvalidOperationException("RenderDoc launch requires Diagnostics.Enabled.");
                var session = await RenderDocSession.LaunchAsync(diagnostics, executable, workingDirectory,
                    storage.Arguments, storage.Environment, Path.Combine(resultDirectory, "diagnostics", "_renderdoc-session"),
                    cancellationToken).ConfigureAwait(false);
                return new TargetLaunch(session.TargetProcess, null, null, session, "renderdoc", false) { _storage = storage };
            }
            var startInfo = new ProcessStartInfo
            {
                FileName = executable, WorkingDirectory = workingDirectory, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = false
            };
            foreach (var argument in storage.Arguments) startInfo.ArgumentList.Add(argument);
            startInfo.Environment.Clear();
            foreach (var variable in storage.Environment) startInfo.Environment[variable.Key] = variable.Value;
            var helper = Path.Combine(AppContext.BaseDirectory, "tools", "runner_posix.py");
            var python = ToolProcess.ResolveExecutable(diagnostics.PythonExecutable);
            if (OperatingSystem.IsLinux() && diagnostics.CrashReports.PreserveNativeExitStatus && File.Exists(helper) && python is not null)
            {
                var native = await PosixTarget.StartAsync(python, helper, startInfo, resultDirectory, cancellationToken).ConfigureAwait(false);
                return new TargetLaunch(native.Target, native.Supervisor.StandardOutput.BaseStream,
                    native.Supervisor.StandardError.BaseStream, null, "direct", false) { _posix = native, _storage = storage };
            }
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("Process.Start returned false.");
            }
            return new TargetLaunch(process, process.StandardOutput.BaseStream, process.StandardError.BaseStream,
                null, "direct", true) { _storage = storage };
        }
        catch
        {
            // A failed start is not permission to claim the target ran. Preserve
            // prepared paths and any partial state without an automatic retry.
            await storage.CompleteAsync(false).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var stopped = false;
        try { stopped = (_posix?.Supervisor ?? Process).HasExited; }
        catch (InvalidOperationException) { }
        if (_storage is not null) await _storage.CompleteAsync(stopped).ConfigureAwait(false);
        if (RenderDoc is not null) await RenderDoc.DisposeAsync().ConfigureAwait(false);
        if (_posix is not null) await _posix.DisposeAsync().ConfigureAwait(false);
        if (_ownsProcess) Process.Dispose();
    }
}
