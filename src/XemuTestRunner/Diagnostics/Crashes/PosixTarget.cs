using System.Diagnostics;
using System.Text.Json;
using XemuTestRunner.Config;

namespace XemuTestRunner.Diagnostics;

internal sealed class PosixTarget : IAsyncDisposable
{
    public Process Supervisor { get; }
    public Process Target { get; }
    private readonly string _root;
    private readonly string _identity;
    private NativeExitStatus? _exit;
    private PosixTarget(Process supervisor, Process target, string root, string identity)
    { Supervisor = supervisor; Target = target; _root = root; _identity = identity; }

    public NativeExitStatus? ReadExit()
    {
        if (_exit is not null) return _exit;
        var file = Path.Combine(_root, "exit-native.json");
        if (!File.Exists(file) || new FileInfo(file).Length > 4096) return null;
        try
        {
            var value = JsonSerializer.Deserialize<NativeExitStatus>(File.ReadAllText(file), ConfigLoader.JsonOptions);
            if (value is null || value.Identity != _identity || value.Pid != Target.Id ||
                (value.Signal is null) == (value.ExitCode is null) || value.Signal is <= 0 or > 64 || value.ExitCode is < 0 or > 255)
                return null;
            return _exit = value;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public static async Task<PosixTarget> StartAsync(string python, string helper, ProcessStartInfo targetInfo,
        string resultDirectory, CancellationToken ct)
    {
        var root = Path.Combine(resultDirectory, "crash", "native");
        Directory.CreateDirectory(root);
        var identity = Guid.NewGuid().ToString("N");
        var info = new ProcessStartInfo(python)
        {
            WorkingDirectory = targetInfo.WorkingDirectory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in new[] { "-I", helper, root, identity, targetInfo.FileName }.Concat(targetInfo.ArgumentList)) info.ArgumentList.Add(argument);
        foreach (var item in targetInfo.Environment) info.Environment[item.Key] = item.Value;
        var supervisor = new Process { StartInfo = info };
        Process? target = null;
        try
        {
            if (!supervisor.Start()) throw new IOException("Native-exit supervisor did not start.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(8000);
            var ready = Path.Combine(root, "ready.json");
            while (!File.Exists(ready))
            {
                if (supervisor.HasExited) throw new IOException("Native-exit supervisor exited before its launch handshake.");
                await Task.Delay(10, deadline.Token).ConfigureAwait(false);
            }
            if (new FileInfo(ready).Length > 4096) throw new InvalidDataException("Native launch handshake is oversized.");
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(ready, deadline.Token).ConfigureAwait(false));
            if (document.RootElement.GetProperty("identity").GetString() != identity) throw new InvalidDataException("Native launch identity mismatch.");
            target = Process.GetProcessById(document.RootElement.GetProperty("pid").GetInt32());
            // The child is still blocked on a private pipe. Observe its start
            // identity before permitting exec, including immediate-start crashes.
            _ = target.StartTime;
            await File.WriteAllTextAsync(Path.Combine(root, "start"), "1", deadline.Token).ConfigureAwait(false);
            return new PosixTarget(supervisor, target, root, identity);
        }
        catch
        {
            try { if (!supervisor.HasExited) supervisor.Kill(true); } catch (InvalidOperationException) { }
            try { await supervisor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (Exception) { }
            target?.Dispose(); supervisor.Dispose(); throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _ = ReadExit();
        try
        {
            if (!Supervisor.HasExited) Supervisor.Kill(true);
            await Supervisor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception) { }
        Target.Dispose(); Supervisor.Dispose();
    }
}
