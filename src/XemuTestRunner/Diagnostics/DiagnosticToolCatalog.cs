namespace XemuTestRunner.Diagnostics;

public sealed class DiagnosticToolCatalog
{
    private readonly DiagnosticsOptions _options;
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private IReadOnlyList<ToolCapability>? _cached;
    private DateTimeOffset _cachedUtc;

    public DiagnosticToolCatalog(DiagnosticsOptions options) => _options = options;

    public IReadOnlyList<ToolCapability> Probe()
    {
        var toolSpecs = new List<(string Name, string Executable, bool Applicable)>
        {
            ("WPR", _options.WprExecutable, OperatingSystem.IsWindows()),
            ("xperf", _options.XperfExecutable, OperatingSystem.IsWindows()),
            ("perf", _options.PerfExecutable, OperatingSystem.IsLinux()),
            ("GDB", _options.GdbExecutable, OperatingSystem.IsLinux()),
            ("ProcDump", _options.ProcDumpExecutable, OperatingSystem.IsWindows()),
            ("RenderDoc CLI", _options.RenderDocCommand, OperatingSystem.IsWindows() || OperatingSystem.IsLinux()),
            ("Python", _options.PythonExecutable, true),
            ("addr2line", _options.Addr2LineExecutable, true)
        };

        return toolSpecs.Select(tool =>
        {
            if (!tool.Applicable)
                return new ToolCapability(tool.Name, false, tool.Executable, null, "Not applicable to this operating system.");

            var resolved = ToolProcess.ResolveExecutable(tool.Executable);
            return new ToolCapability(
                tool.Name,
                resolved is not null,
                tool.Executable,
                resolved,
                resolved is null ? "Not found on PATH or configured path." : "Available.");
        }).ToArray();
    }

    public async Task<IReadOnlyList<ToolCapability>> ProbeAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null && DateTimeOffset.UtcNow - _cachedUtc < TimeSpan.FromSeconds(30))
            return _cached;

        await _probeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null && DateTimeOffset.UtcNow - _cachedUtc < TimeSpan.FromSeconds(30))
                return _cached;

            var results = Probe().ToList();
            results.Add(await ProbeRenderDocPythonAsync(cancellationToken).ConfigureAwait(false));
            _cached = results.ToArray();
            _cachedUtc = DateTimeOffset.UtcNow;
            return _cached;
        }
        finally
        {
            _probeGate.Release();
        }
    }

    public ToolCapability Get(string name) =>
        Probe().FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? new ToolCapability(name, false, name, null, "Unknown tool.");

    public async Task<ToolCapability> ProbeRenderDocPythonAsync(CancellationToken cancellationToken)
    {
        var python = ToolProcess.ResolveExecutable(_options.PythonExecutable);
        if (python is null)
            return new("RenderDoc Python", false, _options.PythonExecutable, null, "Python executable is unavailable.");

        var environment = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(_options.RenderDocPythonPath))
        {
            var existing = Environment.GetEnvironmentVariable("PYTHONPATH");
            environment["PYTHONPATH"] = string.IsNullOrWhiteSpace(existing)
                ? _options.RenderDocPythonPath
                : _options.RenderDocPythonPath + Path.PathSeparator + existing;
        }

        try
        {
            var run = await ToolProcess.RunAsync(
                python,
                ["-c", "import renderdoc; print(getattr(renderdoc, '__file__', 'renderdoc'))"],
                Environment.CurrentDirectory,
                Math.Min(_options.ToolTimeoutMs, 10000),
                cancellationToken,
                environment).ConfigureAwait(false);

            return new(
                "RenderDoc Python",
                run.ExitCode == 0,
                _options.PythonExecutable,
                python,
                run.ExitCode == 0
                    ? "RenderDoc Python module import succeeded: " + run.StandardOutput.Trim()
                    : "RenderDoc Python module import failed: " + run.StandardError.Trim());
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            return new("RenderDoc Python", false, _options.PythonExecutable, python, ex.Message);
        }
    }
}
