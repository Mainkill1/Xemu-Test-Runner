using System.Diagnostics;
using System.Text;

namespace XemuTestRunner.Diagnostics;

internal sealed record ToolRunResult(
    string Executable,
    IReadOnlyList<string> Arguments,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration);

internal sealed class LongRunningTool : IAsyncDisposable
{
    public Process Process { get; }
    public Task StandardOutputPump { get; }
    public Task StandardErrorPump { get; }

    public LongRunningTool(Process process, Task stdout, Task stderr)
    {
        Process = process;
        StandardOutputPump = stdout;
        StandardErrorPump = stderr;
    }

    public async Task SettleOutputAsync(TimeSpan timeout)
    {
        await Task.WhenAll(StandardOutputPump, StandardErrorPump).WaitAsync(timeout).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        Process.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal static class ToolProcess
{
    public static string? ResolveExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
            return null;

        if (Path.IsPathRooted(executable) || executable.Contains(Path.DirectorySeparatorChar) ||
            executable.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(executable) ? Path.GetFullPath(executable) : null;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory.Trim(), executable);
                if (OperatingSystem.IsWindows() && Path.GetExtension(candidate).Length == 0)
                    candidate += extension.ToLowerInvariant();
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
        }
        return null;
    }

    public static async Task<ToolRunResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        int timeoutMs,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var resolved = ResolveExecutable(executable)
            ?? throw new FileNotFoundException($"Diagnostic tool not found: {executable}");
        var args = arguments.ToArray();
        var info = new ProcessStartInfo
        {
            FileName = resolved,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        if (environment is not null)
            foreach (var pair in environment)
                info.Environment[pair.Key] = pair.Value;

        using var process = new Process { StartInfo = info };
        var started = Stopwatch.GetTimestamp();
        if (!process.Start())
            throw new InvalidOperationException($"Failed to start diagnostic tool: {resolved}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Diagnostic tool exceeded {timeoutMs} ms: {resolved}");
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new(
            resolved,
            args,
            process.ExitCode,
            stdout,
            stderr,
            Stopwatch.GetElapsedTime(started));
    }

    public static async Task<LongRunningTool> StartLongRunningAsync(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        string stdoutPath,
        string stderrPath,
        CancellationToken cancellationToken)
    {
        var resolved = ResolveExecutable(executable)
            ?? throw new FileNotFoundException($"Diagnostic tool not found: {executable}");
        var info = new ProcessStartInfo
        {
            FileName = resolved,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in arguments)
            info.ArgumentList.Add(arg);

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"Failed to start diagnostic tool: {resolved}");
        }

        var stdout = PumpAsync(process.StandardOutput, stdoutPath, cancellationToken);
        var stderr = PumpAsync(process.StandardError, stderrPath, cancellationToken);
        await Task.Yield();
        return new LongRunningTool(process, stdout, stderr);
    }

    private static async Task PumpAsync(StreamReader reader, string path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await reader.BaseStream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteRunEvidenceAsync(string directory, string name, ToolRunResult run, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, name + ".stdout.txt"), run.StandardOutput, ct);
        await File.WriteAllTextAsync(Path.Combine(directory, name + ".stderr.txt"), run.StandardError, ct);
        await File.WriteAllTextAsync(Path.Combine(directory, name + ".command.txt"),
            run.Executable + Environment.NewLine + string.Join(Environment.NewLine, run.Arguments), ct);
    }
}
