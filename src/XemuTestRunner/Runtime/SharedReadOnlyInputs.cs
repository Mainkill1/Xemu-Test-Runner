using System.Security.Cryptography;

namespace XemuTestRunner.Runtime;

/// <summary>Resolve only catalog-owned firmware/DVD paths; never mount or copy a shared writable HDD.</summary>
internal static class SharedReadOnlyInputs
{
    public static async Task PrepareAsync(RunIsolationDefinition policy, RuntimeMaterialization? runtime,
        RunStorageReport report, CancellationToken ct)
    {
        if (policy.ReadOnlyAssets is null) return;
        if (runtime is null) throw new InvalidDataException("Shared inputs require runner-owned RuntimeState metadata to resolve the workspace.");
        var runtimeParent = Directory.GetParent(Path.GetFullPath(runtime.Directory));
        if (runtimeParent?.Name != "Runtime" || runtimeParent.Parent is null)
            throw new InvalidDataException("Shared input workspace is not a runner Runtime directory.");
        var catalog = new DiskAssetCatalog(runtimeParent.Parent.FullName);
        report.ReadOnlyAssets = [];
        foreach (var asset in policy.ReadOnlyAssets)
        {
            ct.ThrowIfCancellationRequested();
            var manifest = catalog.GetRequired(asset.AssetId);
            if (manifest.Kind != "readonly-input") throw new InvalidDataException("Shared firmware/DVD must use a readonly-input catalog asset, not a writable disk seed.");
            if (!manifest.Sha256.Equals(asset.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Shared input catalog SHA-256 does not match the selected test.");
            if (!catalog.IsReady(manifest)) throw new InvalidDataException("Shared input upload is incomplete: " + asset.AssetId);
            var path = catalog.ContentPath(asset.AssetId);
            var digest = await HashAsync(path, manifest.Length, ct).ConfigureAwait(false);
            if (!digest.Equals(asset.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Shared input SHA-256 differs from its pinned content: " + asset.AssetId);
            report.ReadOnlyAssets.Add(new ReadOnlyAssetReport
            {
                Field = asset.Field, AssetId = asset.AssetId, Path = path, Bytes = manifest.Length,
                ExpectedSha256 = asset.ExpectedSha256.ToLowerInvariant(), BeforeSha256 = digest
            });
        }
    }

    public static async Task VerifyAfterAsync(RunStorageReport report, CancellationToken ct)
    {
        if (report.ReadOnlyAssets is null) return;
        foreach (var asset in report.ReadOnlyAssets)
        {
            asset.AfterSha256 = await HashAsync(asset.Path, asset.Bytes, ct).ConfigureAwait(false);
            if (asset.AfterSha256 != asset.ExpectedSha256 || asset.BeforeSha256 != asset.ExpectedSha256)
            {
                report.Issues.Add("readonly_input_changed:" + asset.Field);
                throw new InvalidDataException("Shared input content changed during the attempt: " + asset.AssetId);
            }
        }
    }

    private static async Task<string> HashAsync(string path, long length, CancellationToken ct)
    {
        RunStateInventory.NoLinks(path);
        var before = new FileInfo(path);
        var modified = before.LastWriteTimeUtc;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length != length) throw new InvalidDataException("Shared input length changed.");
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false)).ToLowerInvariant();
        var after = new FileInfo(path);
        if (file.Length != length || after.Length != length || after.LastWriteTimeUtc != modified)
            throw new InvalidDataException("Shared input changed while SHA-256 was being verified.");
        return hash;
    }
}
