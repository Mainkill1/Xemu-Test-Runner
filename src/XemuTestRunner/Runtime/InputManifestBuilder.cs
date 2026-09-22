using System.Security.Cryptography;
using XemuTestRunner.Config;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Runtime;

public sealed record InputManifestEntry(
    string Path,
    string Role,
    long Bytes,
    DateTime LastWriteUtc,
    string? Sha256,
    string? ExpectedSha256,
    bool Verified);

public sealed record InputManifest(
    string JobId,
    DateTimeOffset CreatedUtc,
    string PackageDirectory,
    string JobManifestSha256,
    string? ExecutableSha256,
    IReadOnlyList<InputManifestEntry> Inputs,
    IReadOnlyList<RuntimeFileMaterialization> RuntimeFiles);

public static class InputManifestBuilder
{
    public static async Task<InputManifest> BuildAsync(
        JobDefinition job,
        string packageDirectory,
        string frozenJobManifest,
        string? executableSha256,
        RuntimeMaterialization? runtime,
        CancellationToken cancellationToken)
    {
        var jobHash = await HashFileAsync(
            frozenJobManifest,
            cancellationToken).ConfigureAwait(false);

        var definitions = new Dictionary<string, InputIdentityDefinition>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

        foreach (var required in job.RequiredFiles)
        {
            definitions[required] = new InputIdentityDefinition
            {
                Path = required,
                Role = "required",
                Hash = false
            };
        }

        foreach (var input in job.Inputs)
            definitions[input.Path] = input;

        var entries = new List<InputManifestEntry>();

        foreach (var definition in definitions.Values)
        {
            var path = JobDefinition.ResolveInsidePackage(
                packageDirectory,
                definition.Path);
            var info = new FileInfo(path);

            if (!info.Exists)
                throw new FileNotFoundException(
                    $"Declared input '{definition.Path}' is missing.",
                    path);

            var shouldHash =
                definition.Hash ||
                !string.IsNullOrWhiteSpace(
                    definition.ExpectedSha256);

            string? actual = null;
            if (shouldHash)
            {
                actual = await HashFileAsync(
                    path,
                    cancellationToken).ConfigureAwait(false);
            }

            var verified =
                string.IsNullOrWhiteSpace(
                    definition.ExpectedSha256) ||
                actual?.Equals(
                    definition.ExpectedSha256,
                    StringComparison.OrdinalIgnoreCase) == true;

            if (!verified)
            {
                throw new InvalidDataException(
                    $"Input '{definition.Path}' SHA-256 mismatch. " +
                    $"Expected {definition.ExpectedSha256}; actual {actual ?? "not calculated"}.");
            }

            entries.Add(new InputManifestEntry(
                definition.Path,
                definition.Role,
                info.Length,
                info.LastWriteTimeUtc,
                actual,
                definition.ExpectedSha256,
                verified));
        }

        return new InputManifest(
            job.Id,
            DateTimeOffset.UtcNow,
            Path.GetFullPath(packageDirectory),
            jobHash,
            executableSha256,
            entries,
            runtime?.Files ?? []);
    }

    public static void Write(
        string resultDirectory,
        InputManifest manifest)
    {
        AtomicJson.Write(
            Path.Combine(
                resultDirectory,
                "input-manifest.json"),
            manifest);
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);

        return Convert.ToHexString(
            await SHA256.HashDataAsync(
                file,
                cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
    }
}
