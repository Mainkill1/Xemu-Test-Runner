using System.Security.Cryptography;
using XemuTestRunner.Queue;

namespace XemuTestRunner.Runtime;

public sealed record RuntimeFileMaterialization(
    string Source,
    string Destination,
    long Bytes,
    string Sha256);

public sealed record RuntimeMaterialization(
    string Directory,
    IReadOnlyList<RuntimeFileMaterialization> Files)
{
    public bool HasFiles => Files.Count > 0;
}

public static class RuntimeStateManager
{
    public static async Task<RuntimeMaterialization?> MaterializeAsync(
        RuntimeStateDefinition definition,
        string packageDirectory,
        string workspace,
        string runId,
        CancellationToken cancellationToken)
    {
        if (!definition.Enabled && definition.Files.Count == 0)
            return null;

        var runtimeRoot = Path.Combine(
            workspace,
            "Runtime",
            runId);
        Directory.CreateDirectory(runtimeRoot);

        var files = new List<RuntimeFileMaterialization>();

        try
        {
            foreach (var entry in definition.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(entry.Source) ||
                    string.IsNullOrWhiteSpace(entry.Destination))
                    throw new InvalidDataException(
                        "RuntimeState files require Source and Destination.");

                var source = JobDefinition.ResolveInsidePackage(
                    packageDirectory,
                    entry.Source);
                if (!File.Exists(source))
                    throw new FileNotFoundException(
                        "Runtime-state seed file does not exist.",
                        source);

                var destination = ResolveInside(
                    runtimeRoot,
                    entry.Destination);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(destination)!);

                var copied = await CopyWithHashAsync(
                    source,
                    destination,
                    cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(
                        entry.ExpectedSha256) &&
                    !copied.Sha256.Equals(
                        entry.ExpectedSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Runtime seed '{entry.Source}' SHA-256 mismatch. " +
                        $"Expected {entry.ExpectedSha256}; actual {copied.Sha256}.");
                }

                files.Add(new RuntimeFileMaterialization(
                    entry.Source,
                    entry.Destination,
                    copied.Bytes,
                    copied.Sha256));
            }

            return new RuntimeMaterialization(
                runtimeRoot,
                files);
        }
        catch
        {
            TryDeleteDirectory(runtimeRoot);
            throw;
        }
    }

    public static string Expand(
        string value,
        string packageDirectory,
        string resultDirectory,
        string runId,
        RuntimeMaterialization? runtime)
    {
        if (value.Contains(
                "{runtimeDir}",
                StringComparison.Ordinal) &&
            runtime is null)
        {
            throw new InvalidDataException(
                "The job uses {runtimeDir} but RuntimeState is not enabled.");
        }

        return value
            .Replace(
                "{packageDir}",
                packageDirectory,
                StringComparison.Ordinal)
            .Replace(
                "{resultDir}",
                resultDirectory,
                StringComparison.Ordinal)
            .Replace(
                "{runId}",
                runId,
                StringComparison.Ordinal)
            .Replace(
                "{runtimeDir}",
                runtime?.Directory ?? "",
                StringComparison.Ordinal);
    }

    public static void Cleanup(
        RuntimeStateDefinition definition,
        RuntimeMaterialization? runtime,
        bool success)
    {
        if (runtime is null)
            return;

        var keep = success
            ? definition.KeepOnSuccess
            : definition.KeepOnFailure;

        if (!keep)
            TryDeleteDirectory(runtime.Directory);
    }

    public static string ResolveInside(
        string root,
        string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath))
            throw new InvalidDataException(
                "Runtime paths must be non-empty and relative.");

        var canonicalRoot = Path.GetFullPath(root)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(
            Path.Combine(canonicalRoot, relativePath));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!full.StartsWith(
                canonicalRoot + Path.DirectorySeparatorChar,
                comparison) &&
            !string.Equals(
                full,
                canonicalRoot,
                comparison))
            throw new InvalidDataException(
                "Runtime path escapes the private runtime directory: " +
                relativePath);

        return full;
    }

    private static async Task<(long Bytes, string Sha256)>
        CopyWithHashAsync(
            string source,
            string destination,
            CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);

        using var hash =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[1024 * 1024];
        long total = 0;

        while (true)
        {
            var count = await input.ReadAsync(
                buffer,
                cancellationToken).ConfigureAwait(false);
            if (count == 0)
                break;

            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(
                buffer.AsMemory(0, count),
                cancellationToken).ConfigureAwait(false);
            total += count;
        }

        await output.FlushAsync(
            cancellationToken).ConfigureAwait(false);

        return (
            total,
            Convert.ToHexString(
                hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
        }
    }
}
