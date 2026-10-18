using System.Diagnostics;
using System.Globalization;
using XemuTestRunner.Queue;

namespace XemuTestRunner.Runtime;

public static class OwnedTargetProcess
{
    public static async Task<Process> ResolveAsync(
        TargetProcessDefinition definition,
        string packageDirectory,
        string resultDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        var evidencePath = ResolveInside(resultDirectory, definition.EvidencePath);
        var expectedExecutable = JobDefinition.ResolveInsidePackage(
            packageDirectory, definition.Executable);
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(definition.TimeoutMs);
        Exception? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return ResolveOnce(definition, evidencePath, expectedExecutable);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                InvalidDataException or ArgumentException or InvalidOperationException)
            {
                last = ex;
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidDataException(
            $"Target-process evidence did not attest the packaged child within {definition.TimeoutMs} ms: {last?.Message}",
            last);
    }

    private static Process ResolveOnce(
        TargetProcessDefinition definition,
        string evidencePath,
        string expectedExecutable)
    {
        var info = new FileInfo(evidencePath);
        if (!info.Exists)
            throw new FileNotFoundException("Target-process evidence has not been published.", evidencePath);
        if (info.Length is <= 0 or > 32768)
            throw new InvalidDataException("Target-process evidence must contain between 1 and 32768 bytes.");

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(evidencePath))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
                throw new InvalidDataException("Target-process evidence contains a malformed field.");
            var key = line[..separator];
            var value = line[(separator + 1)..];
            if (!fields.TryAdd(key, value))
                throw new InvalidDataException($"Target-process evidence repeats field '{key}'.");
        }

        if (!fields.TryGetValue(definition.ProcessIdField, out var pidText) ||
            !int.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
            throw new InvalidDataException("Target-process evidence has an invalid process ID.");
        if (!fields.TryGetValue(definition.ProcessStartTicksField, out var ticksText) ||
            !long.TryParse(ticksText, NumberStyles.None, CultureInfo.InvariantCulture, out var startTicks) || startTicks <= 0)
            throw new InvalidDataException("Target-process evidence has invalid process-start ticks.");
        if (!fields.TryGetValue(definition.ExecutablePathField, out var reportedExecutable) ||
            string.IsNullOrWhiteSpace(reportedExecutable))
            throw new InvalidDataException("Target-process evidence has no executable path.");

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(Path.GetFullPath(reportedExecutable), Path.GetFullPath(expectedExecutable), comparison))
            throw new InvalidDataException("Target-process evidence names an executable outside the selected package target.");

        Process? process = null;
        try
        {
            process = Process.GetProcessById(pid);
            if (process.HasExited)
                throw new InvalidDataException("The attested target process has already exited.");
            if (process.StartTime.ToUniversalTime().Ticks != startTicks)
                throw new InvalidDataException("The attested target process start identity does not match the live process.");
            var liveExecutable = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(liveExecutable) ||
                !string.Equals(Path.GetFullPath(liveExecutable), Path.GetFullPath(expectedExecutable), comparison))
                throw new InvalidDataException("The attested target process executable does not match the selected package target.");
            return process;
        }
        catch
        {
            process?.Dispose();
            throw;
        }
    }

    private static string ResolveInside(string rootDirectory, string relativePath)
    {
        var root = Path.GetFullPath(rootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, comparison) &&
            !string.Equals(full, root, comparison))
            throw new InvalidDataException("TargetProcess EvidencePath escapes the result directory.");
        return full;
    }
}
