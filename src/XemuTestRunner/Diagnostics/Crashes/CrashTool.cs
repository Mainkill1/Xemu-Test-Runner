using System.Diagnostics;
using System.Text;

namespace XemuTestRunner.Diagnostics;

internal sealed record CrashToolResult(string State, int? ExitCode, string Output, string Error);

// Postmortem tools cannot inherit an unbounded wait or output buffer. The token
// covers process exit AND pipe draining; oversized output cancels the collector.
internal static class CrashTool
{
    public static async Task<CrashToolResult> RunAsync(string executable, IEnumerable<string> arguments,
        string directory, CancellationToken cancellationToken, string? binaryOutput = null, long maxBytes = 262144)
    {
        var resolved = ToolProcess.ResolveExecutable(executable);
        if (resolved is null) return new("unavailable", null, "", "tool_not_found:" + executable);
        var info = new ProcessStartInfo(resolved)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, CreateNoWindow = true, WorkingDirectory = directory
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["DEBUGINFOD_URLS"] = "";
        info.Environment["_NT_SYMBOL_PATH"] = "";
        info.Environment["LC_ALL"] = "C";
        using var process = new Process { StartInfo = info };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stdout = new MemoryStream();
        var stderr = new MemoryStream();
        FileStream? binary = null;
        var limitHit = 0;
        var memoryGate = new object();
        Task? outputTask = null, errorTask = null;
        try
        {
            if (binaryOutput is not null)
                binary = new FileStream(binaryOutput, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous);
            if (!process.Start()) return new("failed", null, "", "tool_start_failed");
            process.StandardInput.Close();
            outputTask = Pump(process.StandardOutput.BaseStream, (Stream?)binary ?? stdout, maxBytes);
            errorTask = Pump(process.StandardError.BaseStream, stderr, 65536);
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), outputTask, errorTask).WaitAsync(deadline.Token).ConfigureAwait(false);
            if (binary is not null) await binary.FlushAsync(deadline.Token).ConfigureAwait(false);
            return new(process.ExitCode == 0 ? "captured" : "failed", process.ExitCode,
                Snapshot(stdout), Snapshot(stderr));
        }
        catch (OperationCanceledException)
        { return new(limitHit != 0 ? "sizeLimit" : "timedOut", null, Snapshot(stdout), Snapshot(stderr)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { return new("failed", null, "", error.Message); }
        finally
        {
            deadline.Cancel();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (Exception error) when (error is TimeoutException or InvalidOperationException) { }
            // Closing our read handles also breaks a pipe held open by a tool's
            // descendant. Never wait forever for a grandchild to close stdout.
            try { process.StandardOutput.Dispose(); process.StandardError.Dispose(); } catch (InvalidOperationException) { }
            try { await Task.WhenAll(outputTask ?? Task.CompletedTask, errorTask ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (Exception) { }
            if (binary is not null) await binary.DisposeAsync().ConfigureAwait(false);
            lock (memoryGate) { stdout.Dispose(); stderr.Dispose(); }
        }

        string Snapshot(MemoryStream stream)
        {
            lock (memoryGate) return Encoding.UTF8.GetString(stream.ToArray());
        }

        async Task Pump(Stream source, Stream target, long limit)
        {
            var buffer = new byte[65536];
            long written = 0;
            while (true)
            {
                var count = await source.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
                if (count == 0) return;
                var retained = (int)Math.Min(count, Math.Max(0, limit - written));
                if (retained > 0)
                {
                    if (target is MemoryStream memory)
                    {
                        lock (memoryGate) memory.Write(buffer, 0, retained);
                    }
                    else await target.WriteAsync(buffer.AsMemory(0, retained), deadline.Token).ConfigureAwait(false);
                }
                written += retained;
                if (retained != count)
                {
                    Interlocked.Exchange(ref limitHit, 1);
                    deadline.Cancel();
                    return;
                }
            }
        }
    }
}
