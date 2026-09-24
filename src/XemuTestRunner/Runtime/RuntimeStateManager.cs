using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using XemuTestRunner.Config;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Runtime;

public sealed record RuntimeFileMaterialization(
    string Source,
    string Destination,
    long Bytes,
    string Sha256)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DiskAssetId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AssetKind { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Retention { get; init; }
}

public sealed record RuntimeMaterialization(
    string Directory,
    IReadOnlyList<RuntimeFileMaterialization> Files)
{
    public bool HasFiles => Files.Count > 0;
}

public sealed class RuntimeCleanupFile
{
    [JsonPropertyName("assetId")] public string AssetId { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("retention")] public string Retention { get; set; } = "";
    [JsonPropertyName("deleted")] public bool Deleted { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed class RuntimeCleanupReceipt
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("runId")] public string RunId { get; set; } = "";
    [JsonPropertyName("state")] public string State { get; set; } = "pending";
    [JsonPropertyName("targetStopped")] public bool TargetStopped { get; set; }
    [JsonPropertyName("evidenceFinalized")] public bool EvidenceFinalized { get; set; }
    [JsonPropertyName("runtimeDirectory")] public string RuntimeDirectory { get; set; } = "";
    [JsonPropertyName("files")] public List<RuntimeCleanupFile> Files { get; set; } = [];
    [JsonPropertyName("updatedUtc")] public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
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
        if (!definition.Enabled && definition.Files.Count == 0 && definition.DiskAssets.Count == 0)
            return null;

        var runtimeRoot = Path.Combine(workspace, "Runtime", runId);
        RunStateInventory.NoLinks(runtimeRoot);
        Directory.CreateDirectory(runtimeRoot);
        var files = new List<RuntimeFileMaterialization>();

        try
        {
            foreach (var entry in definition.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(entry.Source) || string.IsNullOrWhiteSpace(entry.Destination))
                    throw new InvalidDataException("RuntimeState files require Source and Destination.");
                var source = JobDefinition.ResolveInsidePackage(packageDirectory, entry.Source);
                if (!File.Exists(source)) throw new FileNotFoundException("Runtime-state seed file does not exist.", source);
                var destination = ResolveInside(runtimeRoot, entry.Destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var copied = await CopyWithHashAsync(source, destination, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(entry.ExpectedSha256) &&
                    !copied.Sha256.Equals(entry.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Runtime seed '{entry.Source}' SHA-256 mismatch. Expected {entry.ExpectedSha256}; actual {copied.Sha256}.");
                files.Add(new RuntimeFileMaterialization(entry.Source, entry.Destination, copied.Bytes, copied.Sha256));
            }

            if (definition.DiskAssets.Count > 0)
            {
                var catalog = new DiskAssetCatalog(workspace);
                foreach (var entry in definition.DiskAssets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var manifest = catalog.GetRequired(entry.AssetId);
                    if (!manifest.Sha256.Equals(entry.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(
                            $"Disk asset '{entry.AssetId}' SHA-256 mismatch. Expected {entry.ExpectedSha256}; catalog pins {manifest.Sha256}.");
                    if (!catalog.IsReady(manifest))
                        throw new InvalidDataException($"Disk asset '{entry.AssetId}' is not fully uploaded.");
                    var source = catalog.ContentPath(entry.AssetId);
                    var destination = ResolveInside(runtimeRoot, entry.Destination);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await RunStateInventory.CopyAsync(source, destination, manifest.Length,
                        entry.ExpectedSha256, cancellationToken).ConfigureAwait(false);
                    files.Add(new RuntimeFileMaterialization(
                        "disk-asset:" + entry.AssetId, entry.Destination, manifest.Length,
                        entry.ExpectedSha256.ToLowerInvariant())
                    {
                        DiskAssetId = entry.AssetId,
                        AssetKind = manifest.Kind,
                        Retention = entry.Retention
                    });
                }
            }

            return new RuntimeMaterialization(runtimeRoot, files);
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
        if (value.Contains("{runtimeDir}", StringComparison.Ordinal) && runtime is null)
            throw new InvalidDataException("The job uses {runtimeDir} but RuntimeState is not enabled.");
        return value
            .Replace("{packageDir}", packageDirectory, StringComparison.Ordinal)
            .Replace("{resultDir}", resultDirectory, StringComparison.Ordinal)
            .Replace("{runId}", runId, StringComparison.Ordinal)
            .Replace("{runtimeDir}", runtime?.Directory ?? "", StringComparison.Ordinal);
    }

    public static void Cleanup(RuntimeStateDefinition definition, RuntimeMaterialization? runtime, bool success)
    {
        if (runtime is null) return;
        var keep = success ? definition.KeepOnSuccess : definition.KeepOnFailure;
        if (runtime.Files.Any(file => file.DiskAssetId is not null)) keep = true;
        if (!keep) TryDeleteDirectory(runtime.Directory);
    }

    public static void Cleanup(
        RuntimeStateDefinition definition,
        RuntimeMaterialization? runtime,
        bool success,
        string resultDirectory,
        bool targetStopped,
        bool evidenceFinalized)
    {
        if (runtime is null) return;
        var keepRoot = success ? definition.KeepOnSuccess : definition.KeepOnFailure;
        var deletable = new List<RuntimeCleanupFile>();

        foreach (var file in runtime.Files.Where(file => file.DiskAssetId is not null))
        {
            var retention = file.Retention ?? "deleteAfterEvidence";
            var delete = retention == "deleteAfterEvidence" ||
                (retention == "keepOnFailure" && success);
            if (retention == "keep" || (retention == "keepOnFailure" && !success))
            {
                keepRoot = true;
                continue;
            }
            if (!delete) { keepRoot = true; continue; }
            if (!targetStopped || !evidenceFinalized)
            {
                keepRoot = true;
                continue;
            }
            deletable.Add(new RuntimeCleanupFile
            {
                AssetId = file.DiskAssetId!,
                Path = file.Destination,
                Retention = retention
            });
        }

        if (deletable.Count > 0)
        {
            var receipt = new RuntimeCleanupReceipt
            {
                RunId = Path.GetFileName(Path.GetFullPath(resultDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                TargetStopped = targetStopped,
                EvidenceFinalized = evidenceFinalized,
                RuntimeDirectory = runtime.Directory,
                Files = deletable
            };
            var receiptPath = Path.Combine(resultDirectory, "runtime-cleanup.json");
            AtomicJson.Write(receiptPath, receipt);
            ProcessAuthorizedCleanup(receipt, receiptPath, expectedWorkspace: null);
            if (receipt.State != "complete") keepRoot = true;
        }

        if (!keepRoot) TryDeleteDirectory(runtime.Directory);
        else TryDeleteIfEmpty(runtime.Directory);
    }

    public static void RecoverAuthorizedCleanups(string workspace, string results)
    {
        if (!Directory.Exists(results)) return;
        foreach (var result in Directory.EnumerateDirectories(results))
        {
            var receiptPath = Path.Combine(result, "runtime-cleanup.json");
            if (!File.Exists(receiptPath)) continue;
            try
            {
                RunStateInventory.NoLinks(receiptPath);
                if (new FileInfo(receiptPath).Length > 1024 * 1024)
                    throw new InvalidDataException("Runtime cleanup receipt exceeds 1 MiB.");
                var receipt = JsonSerializer.Deserialize<RuntimeCleanupReceipt>(
                    File.ReadAllText(receiptPath), ConfigLoader.JsonOptions)
                    ?? throw new InvalidDataException("Runtime cleanup receipt is empty.");
                if (receipt.SchemaVersion != 1 || receipt.State != "pending" ||
                    !receipt.TargetStopped || !receipt.EvidenceFinalized)
                    continue;
                ProcessAuthorizedCleanup(receipt, receiptPath, workspace);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                InvalidDataException or JsonException or ArgumentException)
            {
                System.Diagnostics.Trace.TraceError(
                    "Runtime cleanup recovery skipped '" + Path.GetFileName(result) + "': " + error.Message);
            }
        }
    }

    private static void ProcessAuthorizedCleanup(
        RuntimeCleanupReceipt receipt, string receiptPath, string? expectedWorkspace)
    {
        if (receipt.SchemaVersion != 1 || !receipt.TargetStopped || !receipt.EvidenceFinalized)
            throw new InvalidDataException("Runtime cleanup is not authorized.");
        if (string.IsNullOrWhiteSpace(receipt.RunId) ||
            Path.GetFileName(receipt.RunId) != receipt.RunId)
            throw new InvalidDataException("Runtime cleanup run ID is invalid.");

        if (expectedWorkspace is not null)
        {
            var expected = Path.GetFullPath(Path.Combine(expectedWorkspace, "Runtime", receipt.RunId));
            if (!SamePath(expected, receipt.RuntimeDirectory))
                throw new InvalidDataException("Runtime cleanup directory does not match the runner workspace.");
        }

        RunStateInventory.NoLinks(receipt.RuntimeDirectory);
        var pending = false;
        foreach (var file in receipt.Files)
        {
            if (file.Retention is not ("deleteAfterEvidence" or "keepOnFailure"))
                throw new InvalidDataException("Cleanup receipt contains a non-deletable retention policy.");
            try
            {
                var path = ResolveInside(receipt.RuntimeDirectory, file.Path);
                RunStateInventory.NoLinks(path);
                if (File.Exists(path)) File.Delete(path);
                else if (Directory.Exists(path)) throw new InvalidDataException("Runtime disk path unexpectedly became a directory.");
                file.Deleted = true;
                file.Error = null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                file.Deleted = false;
                file.Error = error.Message.Length <= 240 ? error.Message : error.Message[..240];
                pending = true;
            }
        }
        receipt.State = pending ? "pending" : "complete";
        receipt.UpdatedUtc = DateTimeOffset.UtcNow;
        AtomicJson.Write(receiptPath, receipt);
        if (!pending) TryDeleteIfEmpty(receipt.RuntimeDirectory);
    }

    public static string ResolveInside(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException("Runtime paths must be non-empty and relative.");
        var canonicalRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(canonicalRoot, relativePath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, comparison) &&
            !string.Equals(full, canonicalRoot, comparison))
            throw new InvalidDataException("Runtime path escapes the private runtime directory: " + relativePath);
        return full;
    }

    private static async Task<(long Bytes, string Sha256)> CopyWithHashAsync(
        string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long total = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            total += count;
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return (total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void TryDeleteIfEmpty(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch { }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch { }
    }
}
