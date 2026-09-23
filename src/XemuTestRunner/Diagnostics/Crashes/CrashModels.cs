namespace XemuTestRunner.Diagnostics;

public sealed class CrashCaptureOptions
{
    public bool Enabled { get; set; } = true;
    public bool PreserveNativeExitStatus { get; set; } = true;
    public bool CollectDumps { get; set; } = true;
    public bool AnalyzeDumps { get; set; } = true;
    public int TimeoutMs { get; set; } = 15000;
    public int BundleTimeoutMs { get; set; } = 15000;
    public int ProviderWaitMs { get; set; } = 2000;
    public long MaxArtifactBytes { get; set; } = 512L * 1024 * 1024;
    public long MaxBundleBytes { get; set; } = 1024L * 1024 * 1024;
    public int MaxBundleFiles { get; set; } = 256;
    public string? WindowsDumpDirectory { get; set; }
    public string CdbExecutable { get; set; } = "cdb.exe";
    public string CoredumpctlExecutable { get; set; } = "coredumpctl";
    public string JournalctlExecutable { get; set; } = "journalctl";
    public List<string> ReportFiles { get; set; } = [];

    public void Validate()
    {
        if (TimeoutMs is < 100 or > 120000 || BundleTimeoutMs is < 100 or > 120000 ||
            ProviderWaitMs < 0 || ProviderWaitMs > TimeoutMs || MaxArtifactBytes is < 1024 or > 8589934592L ||
            MaxBundleBytes < 1024 || MaxBundleBytes > 17179869184L || MaxBundleFiles is < 1 or > 4096 || ReportFiles is null || ReportFiles.Count > 32)
            throw new InvalidDataException("Crash capture time, byte, file or provider limits are invalid.");
        foreach (var path in ReportFiles)
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Replace('\\', '/').Split('/').Any(part => part is ".." or "." or ""))
                throw new InvalidDataException("Crash ReportFiles must be explicit package-relative paths.");
    }
}

public sealed record NativeExitStatus(string Identity, int Pid, int? ExitCode, int? Signal, bool CoreDumped, long FinishedUtcUnixMs);
public sealed record CrashReport(string RunId, string? ExecutableSha256, string Platform,
    int? ProcessId, int? ExitCode, bool RunnerTerminated, bool Crashed, string Source, string? Code,
    string Capture, string Dump, string Analysis, IReadOnlyList<string> TopFrames, IReadOnlyList<string> Issues);
public sealed record DiagnosticBundle(string State, string? Artifact, string? Sha256, long Bytes,
    int IncludedFiles, int OmittedFiles, string? BesideExecutable, string MirrorState, IReadOnlyList<string> Issues);

public static class CrashClassification
{
    public static (bool Crashed, string Source, string? Code) Classify(bool windows, int? exitCode,
        NativeExitStatus? native, bool runnerTerminated)
    {
        if (runnerTerminated) return (false, "runner", null);
        if (!windows)
        {
            if (native is not null)
                return (native.Signal is 3 or 4 or 5 or 6 or 7 or 8 or 11 or 24 or 25 or 31,
                    "waitpid", native.Signal is int signal ? SignalName(signal) : null);
            // The managed exit code loses the WIFEXITED/WIFSIGNALED distinction.
            // In particular, a normal exit(139) is NOT proof of SIGSEGV.
            return (false, "exitCodeOnly", null);
        }
        var code = unchecked((uint)(exitCode ?? 0));
        var knownException = code is 0xC0000005 or 0xC000001D or 0xC0000094 or 0xC0000095 or
            0xC0000096 or 0xC00000FD or 0xC0000374 or 0xC0000409 or 0xC0000602 or
            0x80000003 or 0x80000004 or 0xE0434352 or 0x80131623;
        return (knownException, "windowsExitStatus", knownException ? $"0x{code:X8}" : null);
    }
    public static string SignalName(int signal) => signal switch
    {
        3 => "SIGQUIT", 4 => "SIGILL", 5 => "SIGTRAP", 6 => "SIGABRT", 7 => "SIGBUS",
        8 => "SIGFPE", 9 => "SIGKILL", 11 => "SIGSEGV", 15 => "SIGTERM", 24 => "SIGXCPU",
        25 => "SIGXFSZ", 31 => "SIGSYS", _ => "SIGNAL_" + signal
    };
}
