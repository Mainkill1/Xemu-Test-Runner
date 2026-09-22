using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XemuTestRunner.Queue;

namespace XemuTestRunner.Runtime;

internal sealed record ArtifactInspection(bool Passed, string Detail);

/// <summary>
/// Shared artifact rules for readiness waits and final workload evaluation.
/// Read hash/text from one handle, and bound text/JSON before allocating memory.
/// </summary>
internal static class ArtifactInspector
{
    internal const int MaximumTextBytes = 16 * 1024 * 1024;

    public static string ResolvePath(
        string scope,
        string relativePath,
        string packageDirectory,
        string resultDirectory,
        RuntimeMaterialization? runtime)
    {
        return scope.Trim().ToLowerInvariant() switch
        {
            "package" => JobDefinition.ResolveInsidePackage(packageDirectory, relativePath),
            "result" => RuntimeStateManager.ResolveInside(resultDirectory, relativePath),
            "runtime" when runtime is not null =>
                RuntimeStateManager.ResolveInside(runtime.Directory, relativePath),
            "runtime" => throw new InvalidDataException("Runtime artifact requested without materialized RuntimeState."),
            _ => throw new InvalidDataException($"Unsupported artifact scope '{scope}'.")
        };
    }

    public static async Task<ArtifactInspection> CheckAsync(
        ArtifactCheckDefinition requirement,
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var file = OpenRead(path);
            var length = file.Length;
            if (length < requirement.MinimumBytes)
            {
                return new(false, $"Artifact has {length} bytes; expected at least {requirement.MinimumBytes}.");
            }

            if (!string.IsNullOrWhiteSpace(requirement.ExpectedSha256))
            {
                var hash = Convert.ToHexString(
                    await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
                if (file.Length != length)
                {
                    return new(false, "Artifact changed size while its hash was being read.");
                }

                if (!hash.Equals(requirement.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new(false, $"SHA-256 mismatch. Expected {requirement.ExpectedSha256}; actual {hash}.");
                }
            }

            if (requirement.EqualsText is not null || !string.IsNullOrEmpty(requirement.ContainsText))
            {
                file.Position = 0;
                var content = await ReadBoundedBytesAsync(file, cancellationToken).ConfigureAwait(false);
                using var memory = new MemoryStream(content, writable: false);
                using var reader = new StreamReader(memory, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var text = reader.ReadToEnd();

                if (!string.IsNullOrEmpty(requirement.ContainsText) &&
                    !text.Contains(requirement.ContainsText, StringComparison.Ordinal))
                {
                    return new(false, $"Artifact does not contain required text '{requirement.ContainsText}'.");
                }

                if (requirement.EqualsText is not null &&
                    !text.Equals(requirement.EqualsText, StringComparison.Ordinal))
                {
                    return new(false, "Artifact text does not exactly match the declared value.");
                }
            }

            return new(true, $"{requirement.Scope}:{requirement.Path} satisfied the declared requirement.");
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // Permission failures must not be mistaken for an absent optional file.
            return new(!requirement.MustExist,
                requirement.MustExist ? $"Required artifact is missing: {path}" : $"Optional artifact is absent: {path}");
        }
        catch (Exception exception) when (IsArtifactFailure(exception))
        {
            return new(false, $"Artifact could not be evaluated: {exception.Message}");
        }
    }

    public static async Task<JsonDocument> ReadJsonAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = OpenRead(path);
        var bytes = await ReadBoundedBytesAsync(file, cancellationToken).ConfigureAwait(false);
        var offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        return JsonDocument.Parse(bytes.AsMemory(offset));
    }

    public static bool IsArtifactFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or JsonException;

    private static FileStream OpenRead(string path) => new(
        path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
        64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task<byte[]> ReadBoundedBytesAsync(FileStream file, CancellationToken cancellationToken)
    {
        var length = file.Length;
        if (length > MaximumTextBytes)
        {
            throw new InvalidDataException("Text or JSON artifact exceeds the 16 MiB evaluation limit.");
        }

        var bytes = new byte[checked((int)length)];
        await file.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (file.Length != length)
        {
            throw new IOException("Artifact changed size while it was being read; publish a complete replacement file.");
        }

        return bytes;
    }
}
