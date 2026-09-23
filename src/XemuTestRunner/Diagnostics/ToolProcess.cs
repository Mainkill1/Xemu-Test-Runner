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

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, timeout.Token);
        var stderrTask = ReadBoundedAsync(process.StandardError, timeout.Token);
        try
        {
            // Exiting the tool does not guarantee EOF: descendants may inherit
            // its output handles. The same deadline covers exit and both pipes.
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdoutTask, stderrTask)
                .WaitAsync(timeout.Token).ConfigureAwait(false);
            return new(resolved, args, process.ExitCode, await stdoutTask.ConfigureAwait(false),
                await stderrTask.ConfigureAwait(false), Stopwatch.GetElapsedTime(started));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Diagnostic tool or output drain exceeded {timeoutMs} ms: {resolved}");
        }
        finally
        {
            timeout.Cancel();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (Exception error) when (error is TimeoutException or InvalidOperationException) { }
            process.StandardOutput.Dispose(); process.StandardError.Dispose();
            try { await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException or TimeoutException or InvalidDataException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        const int maximumCharacters = 4 * 1024 * 1024;
        var text = new StringBuilder(); var buffer = new char[16384];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (count == 0) return text.ToString();
            if (count > maximumCharacters - text.Length)
                throw new InvalidDataException("Diagnostic text output exceeds 4 Mi characters; retain large captures as files instead.");
            text.Append(buffer, 0, count);
        }
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
