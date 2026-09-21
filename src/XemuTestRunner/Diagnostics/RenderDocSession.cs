using System.Diagnostics;
using System.Text.Json;

namespace XemuTestRunner.Diagnostics;

public sealed class RenderDocSession : IAsyncDisposable
{
    private readonly Process _helper;
    private readonly SemaphoreSlim _protocolGate = new(1, 1);
    private readonly Task _stderrPump;
    private readonly DiagnosticsOptions _options;

    public Process TargetProcess { get; }
    public int Ident { get; }
    public int TargetPid => TargetProcess.Id;

    private RenderDocSession(
        Process helper,
        Process target,
        int ident,
        Task stderrPump,
        DiagnosticsOptions options)
    {
        _helper = helper;
        TargetProcess = target;
        Ident = ident;
        _stderrPump = stderrPump;
        _options = options;
    }

    public static async Task<RenderDocSession> LaunchAsync(
        DiagnosticsOptions options,
        string executable,
        string workingDirectory,
        IReadOnlyList<string> targetArguments,
        IReadOnlyDictionary<string, string> targetEnvironment,
        string diagnosticsDirectory,
        CancellationToken cancellationToken)
    {
        var python = ToolProcess.ResolveExecutable(options.PythonExecutable)
            ?? throw new FileNotFoundException($"Python not found: {options.PythonExecutable}");
        var script = ResolveToolScript("renderdoc_session.py");
        Directory.CreateDirectory(diagnosticsDirectory);
        var captureTemplate = Path.Combine(diagnosticsDirectory, "renderdoc-capture");

        var info = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        info.ArgumentList.Add(script);
        info.ArgumentList.Add("--exe");
        info.ArgumentList.Add(executable);
        info.ArgumentList.Add("--cwd");
        info.ArgumentList.Add(workingDirectory);
        info.ArgumentList.Add("--capture-template");
        info.ArgumentList.Add(captureTemplate);
        info.ArgumentList.Add("--");
        foreach (var argument in targetArguments)
            info.ArgumentList.Add(argument);
        foreach (var pair in targetEnvironment)
            info.Environment[pair.Key] = pair.Value;
        if (!string.IsNullOrWhiteSpace(options.RenderDocPythonPath))
        {
            var existing = info.Environment.TryGetValue("PYTHONPATH", out var current) ? current : null;
            info.Environment["PYTHONPATH"] = string.IsNullOrWhiteSpace(existing)
                ? options.RenderDocPythonPath
                : options.RenderDocPythonPath + Path.PathSeparator + existing;
        }

        var helper = new Process { StartInfo = info, EnableRaisingEvents = true };
        if (!helper.Start())
        {
            helper.Dispose();
            throw new InvalidOperationException("Failed to start RenderDoc session helper.");
        }

        var stderrPath = Path.Combine(diagnosticsDirectory, "renderdoc-session.stderr.log");
        var stderrPump = PumpAsync(helper.StandardError.BaseStream, stderrPath, CancellationToken.None);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.CaptureFinalizeTimeoutMs);
        try
        {
            while (true)
            {
                var line = await helper.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false)
                    ?? throw new IOException("RenderDoc helper exited before readiness.");
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                var type = root.GetProperty("type").GetString();
                if (type == "fatal")
                    throw new InvalidOperationException(root.GetProperty("error").GetString());
                if (type != "ready")
                    continue;

                var ident = root.GetProperty("ident").GetInt32();
                var pid = root.GetProperty("pid").GetInt32();
                var target = Process.GetProcessById(pid);
                return new RenderDocSession(helper, target, ident, stderrPump, options);
            }
        }
        catch
        {
            try { if (!helper.HasExited) helper.Kill(entireProcessTree: true); } catch { }
            helper.Dispose();
            throw;
        }
    }

    public async Task<IReadOnlyList<string>> CaptureAsync(
        int frames,
        string output,
        bool waitForApplicationTrigger,
        Func<CancellationToken, Task>? applicationTrigger,
        CancellationToken cancellationToken)
    {
        if (frames is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(frames));

        await _protocolGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_options.CaptureFinalizeTimeoutMs);

            var command = JsonSerializer.Serialize(new
            {
                command = waitForApplicationTrigger ? "wait_capture" : "capture",
                frames,
                output = Path.GetFullPath(output)
            });
            await _helper.StandardInput.WriteLineAsync(command.AsMemory(), deadline.Token).ConfigureAwait(false);
            await _helper.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);

            var armed = false;
            var expected = frames;
            var captures = new List<string>(frames);

            while (captures.Count < expected)
            {
                var line = await _helper.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false)
                    ?? throw new IOException("RenderDoc helper disconnected.");
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                var type = root.GetProperty("type").GetString();

                if (type is "fatal" or "disconnected")
                    throw new InvalidOperationException(
                        root.TryGetProperty("error", out var error) ? error.GetString() : type);
                if (type == "error")
                    throw new InvalidOperationException(root.GetProperty("error").GetString());

                if (type == "armed" && !armed)
                {
                    armed = true;
                    if (root.TryGetProperty("expected", out var expectedElement) &&
                        expectedElement.TryGetInt32(out var helperExpected) &&
                        helperExpected > 0)
                        expected = helperExpected;

                    if (waitForApplicationTrigger)
                    {
                        if (applicationTrigger is null)
                            throw new InvalidOperationException(
                                "RenderDoc application trigger callback is required.");
                        await applicationTrigger(deadline.Token).ConfigureAwait(false);
                    }
                    continue;
                }

                if (type == "capture")
                {
                    var path = root.GetProperty("output").GetString()
                        ?? throw new InvalidDataException("RenderDoc helper returned no output path.");
                    if (!File.Exists(path))
                        throw new IOException($"RenderDoc capture was reported but not found: {path}");
                    captures.Add(path);
                }
            }

            return captures;
        }
        finally
        {
            _protocolGate.Release();
        }
    }

    public async Task<string?> AnalyzeAsync(string capture, string output, CancellationToken cancellationToken)
    {
        var script = ResolveToolScript("renderdoc_analyze.py");
        var environment = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(_options.RenderDocPythonPath))
        {
            var existing = Environment.GetEnvironmentVariable("PYTHONPATH");
            environment["PYTHONPATH"] = string.IsNullOrWhiteSpace(existing)
                ? _options.RenderDocPythonPath
                : _options.RenderDocPythonPath + Path.PathSeparator + existing;
        }

        var run = await ToolProcess.RunAsync(
            _options.PythonExecutable,
            [script, "--capture", capture, "--output", output],
            Path.GetDirectoryName(capture)!,
            _options.CaptureFinalizeTimeoutMs,
            cancellationToken,
            environment).ConfigureAwait(false);
        var evidenceName = Path.GetFileNameWithoutExtension(output) + "-analysis";
        await ToolProcess.WriteRunEvidenceAsync(
            Path.GetDirectoryName(output)!,
            evidenceName,
            run,
            cancellationToken);
        return run.ExitCode == 0 && File.Exists(output) ? output : null;
    }

    public async ValueTask DisposeAsync()
    {
        await _protocolGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_helper.HasExited)
            {
                try
                {
                    await _helper.StandardInput.WriteLineAsync("{\"command\":\"quit\"}").ConfigureAwait(false);
                    await _helper.StandardInput.FlushAsync().ConfigureAwait(false);
                    await _helper.WaitForExitAsync().WaitAsync(
                        TimeSpan.FromMilliseconds(_options.ToolTimeoutMs)).ConfigureAwait(false);
                }
                catch
                {
                    try { if (!_helper.HasExited) _helper.Kill(entireProcessTree: true); } catch { }
                }
            }
            try { await _stderrPump.WaitAsync(TimeSpan.FromMilliseconds(_options.ToolTimeoutMs)); } catch { }
            TargetProcess.Dispose();
            _helper.Dispose();
        }
        finally
        {
            _protocolGate.Release();
            _protocolGate.Dispose();
        }
    }

    private static string ResolveToolScript(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "tools", name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Bundled diagnostic helper not found: {path}");
        return path;
    }

    private static async Task PumpAsync(Stream source, string path, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
    }
}
