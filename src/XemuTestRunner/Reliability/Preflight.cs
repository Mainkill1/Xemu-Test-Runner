using System.Security.Cryptography;
using XemuTestRunner.Queue;

namespace XemuTestRunner.Reliability;

public sealed record PreflightCheck(string Name, bool Passed, string Detail);
public sealed record PreflightReport(bool Passed, string? ExecutableSha256, IReadOnlyList<PreflightCheck> Checks);

public static class Preflight
{
    public static async Task<PreflightReport> CheckAsync(JobDefinition job, string package, string resultsRoot,
        PreflightOptions options, CancellationToken ct)
    {
        var checks = new List<PreflightCheck>();
        string? hash = null;
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "unsupported";
        checks.Add(new("target_os", string.IsNullOrWhiteSpace(job.TargetOs) || job.TargetOs.Equals(os, StringComparison.OrdinalIgnoreCase),
            $"Host: {os}; requested: {job.TargetOs ?? "unspecified"}."));
        try
        {
            var executable = JobDefinition.ResolveInsidePackage(package, job.Executable);
            checks.Add(new("executable", File.Exists(executable), job.Executable));
            if (File.Exists(executable))
            {
                var before = new FileInfo(executable);
                var beforeLength = before.Length;
                var beforeWrite = before.LastWriteTimeUtc;

                await using var file = new FileStream(
                    executable,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                hash = Convert.ToHexString(
                    await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();

                var after = new FileInfo(executable);
                after.Refresh();
                var stable = after.Exists &&
                    after.Length == beforeLength &&
                    after.LastWriteTimeUtc == beforeWrite;

                checks.Add(new(
                    "executable_stable",
                    stable,
                    stable
                        ? "Executable metadata remained unchanged while hashing."
                        : "Executable changed while preflight was hashing it. Another process may still be copying or replacing the build."));

                var expected = job.ExpectedExecutableSha256;
                checks.Add(new(
                    "sha256",
                    string.IsNullOrWhiteSpace(expected) ||
                        hash.Equals(expected, StringComparison.OrdinalIgnoreCase),
                    hash));

                if (OperatingSystem.IsLinux())
                {
                    var mode = File.GetUnixFileMode(executable);
                    checks.Add(new(
                        "executable_bit",
                        (mode & (UnixFileMode.UserExecute |
                                 UnixFileMode.GroupExecute |
                                 UnixFileMode.OtherExecute)) != 0,
                        "The Linux package must retain an executable permission bit."));
                }
            }
            var work = JobDefinition.ResolveInsidePackage(package, job.WorkingDirectory ?? ".");
            checks.Add(new("working_directory", Directory.Exists(work), work));
            foreach (var relative in job.RequiredFiles)
            {
                var path = JobDefinition.ResolveInsidePackage(package, relative);
                checks.Add(new("required_file", File.Exists(path), relative));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        { checks.Add(new("package", false, e.Message)); }
        if (options.MinimumFreeSpaceBytes > 0)
        {
            try
            {
                var path = Path.GetFullPath(resultsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                var drive = DriveInfo.GetDrives().Where(d => d.IsReady && path.StartsWith(
                    d.RootDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
                    .OrderByDescending(d => d.RootDirectory.FullName.Length).First();
                checks.Add(new("free_space", drive.AvailableFreeSpace >= options.MinimumFreeSpaceBytes,
                    $"Available={drive.AvailableFreeSpace}; required={options.MinimumFreeSpaceBytes} bytes."));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            { checks.Add(new("free_space", false, "Cannot determine free space: " + e.Message)); }
        }
        return new(checks.All(c => c.Passed), hash, checks);
    }
}
