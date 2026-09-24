namespace XemuTestRunner.Diagnostics;

public sealed class DiagnosticsOptions
{
    public bool Enabled { get; set; } = true;
    public CrashCaptureOptions CrashReports { get; set; } = new();
    public int ToolTimeoutMs { get; set; } = 15000;
    public int CaptureFinalizeTimeoutMs { get; set; } = 60000;
    public bool AutoHangBundle { get; set; } = true;
    public bool AutoFailureBundle { get; set; }
    public string WprExecutable { get; set; } = "wpr.exe";
    public string XperfExecutable { get; set; } = "xperf.exe";
    public string PerfExecutable { get; set; } = "perf";
    public string GdbExecutable { get; set; } = "gdb";
    public string ProcDumpExecutable { get; set; } = "procdump.exe";
    public string RenderDocCommand { get; set; } = "renderdoccmd";
    public string PythonExecutable { get; set; } = OperatingSystem.IsWindows() ? "python.exe" : "python3";
    public string? RenderDocPythonPath { get; set; }
    public string Addr2LineExecutable { get; set; } =
        OperatingSystem.IsWindows() ? "x86_64-w64-mingw32-addr2line.exe" : "addr2line";
}

public sealed class DiagnosticRecipe
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public int DurationMs { get; set; } = 30000;
    public bool PauseBefore { get; set; } = true;
    public bool ResumeDuring { get; set; } = true;
    public bool PauseAfter { get; set; } = true;
    public string? OutputName { get; set; }
    public string? Reason { get; set; }

    // WPR/perf
    public string Profile { get; set; } = "GeneralProfile+GPU";
    public int Frequency { get; set; } = 997;
    public string CallGraph { get; set; } = "dwarf";
    public int? ClockId { get; set; }

    // RenderDoc
    public int Frames { get; set; } = 1;
    public bool TracePgraph { get; set; }
    public string RenderDocTrigger { get; set; } = "xemu-hotkey";
    public bool AnalyzeCapture { get; set; } = true;

    // Guest physical-memory dump
    public ulong Address { get; set; }
    public ulong Size { get; set; }

    // Symbolization
    public string? DebugFile { get; set; }
    public List<string> Addresses { get; set; } = [];

    // QMP / HMP
    public string? QmpCommand { get; set; }
    public Dictionary<string, System.Text.Json.JsonElement> QmpArguments { get; set; } = new(StringComparer.Ordinal);
    public string? MonitorCommand { get; set; }

    // Generic host tool
    public string? ToolExecutable { get; set; }
    public List<string> ToolArguments { get; set; } = [];
    public string? ToolWorkingDirectory { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
            throw new InvalidDataException("Diagnostic recipe Id is required.");
        if (string.IsNullOrWhiteSpace(Type))
            throw new InvalidDataException($"Diagnostic '{Id}' Type is required.");
        if (Addresses is null || QmpArguments is null || ToolArguments is null)
            throw new InvalidDataException($"Diagnostic '{Id}' collections cannot be null.");
        Profile ??= "";
        CallGraph ??= "";
        RenderDocTrigger ??= "";
        if (DurationMs is < 1 or > 86_400_000)
            throw new InvalidDataException($"Diagnostic '{Id}' DurationMs must be between 1 and 86400000.");
        if (Frequency is < 1 or > 100_000)
            throw new InvalidDataException($"Diagnostic '{Id}' Frequency must be between 1 and 100000.");
        if (ClockId < 0)
            throw new InvalidDataException($"Diagnostic '{Id}' ClockId cannot be negative.");
        if (Frames is < 1 or > 120)
            throw new InvalidDataException($"Diagnostic '{Id}' Frames must be between 1 and 120.");

        var type = Type.Trim().ToLowerInvariant();
        if (type is not ("wpr" or "perf" or "renderdoc" or "hang_bundle" or "memory_dump" or "symbolize" or "qmp" or "monitor" or "external"))
            throw new InvalidDataException($"Unsupported diagnostic type '{Type}'.");

        if (type == "memory_dump" && Size == 0)
            throw new InvalidDataException($"Diagnostic '{Id}' memory_dump requires Size > 0.");
        if (type == "symbolize")
        {
            if (string.IsNullOrWhiteSpace(DebugFile) || Addresses.Count == 0)
                throw new InvalidDataException($"Diagnostic '{Id}' symbolize requires DebugFile and Addresses.");
            foreach (var address in Addresses)
                if (string.IsNullOrWhiteSpace(address))
                    throw new InvalidDataException($"Diagnostic '{Id}' contains an empty address.");
        }
        if (type == "qmp" && (string.IsNullOrWhiteSpace(QmpCommand) || QmpCommand.Length > 128))
            throw new InvalidDataException($"Diagnostic '{Id}' qmp requires a command of at most 128 characters.");
        if (type == "monitor" && (string.IsNullOrWhiteSpace(MonitorCommand) || MonitorCommand.Length > 512))
            throw new InvalidDataException($"Diagnostic '{Id}' monitor requires a command of at most 512 characters.");
        if (type == "external")
        {
            if (string.IsNullOrWhiteSpace(ToolExecutable))
                throw new InvalidDataException($"Diagnostic '{Id}' external requires ToolExecutable.");
            if (ToolArguments is null)
                throw new InvalidDataException($"Diagnostic '{Id}' external ToolArguments cannot be null.");
        }
        if (QmpArguments is null)
            throw new InvalidDataException($"Diagnostic '{Id}' QmpArguments cannot be null.");

        var trigger = RenderDocTrigger.Trim().ToLowerInvariant();
        if (type == "renderdoc" && trigger is not ("target-control" or "xemu-hotkey"))
            throw new InvalidDataException($"Diagnostic '{Id}' RenderDocTrigger must be target-control or xemu-hotkey.");
        if (type == "renderdoc" && trigger == "xemu-hotkey" && Frames is not (1 or 5))
            throw new InvalidDataException($"Diagnostic '{Id}' xemu-hotkey capture supports exactly 1 or 5 frames.");
        if (type == "renderdoc" && trigger == "target-control" && Frames != 1)
            throw new InvalidDataException($"Diagnostic '{Id}' target-control capture currently supports exactly 1 frame.");
        if (type == "renderdoc" && trigger == "target-control" && TracePgraph)
            throw new InvalidDataException($"Diagnostic '{Id}' TracePgraph requires the xemu-hotkey trigger.");
    }
}

public sealed record DiagnosticResult(
    string Id,
    string Type,
    string Status,
    DateTimeOffset StartedUtc,
    DateTimeOffset EndedUtc,
    string Directory,
    string? Detail,
    IReadOnlyList<string> Artifacts);

public sealed record ToolCapability(
    string Name,
    bool Available,
    string RequestedExecutable,
    string? ResolvedPath,
    string Detail);

public sealed record DiagnosticSnapshot(
    bool Active,
    string? RunId,
    string? CurrentDiagnostic,
    DateTimeOffset? StartedUtc,
    IReadOnlyList<DiagnosticResult> Completed);
