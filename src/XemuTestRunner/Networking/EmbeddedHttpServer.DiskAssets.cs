using System.Text.Json;
using System.Text.Json.Serialization;
using XemuTestRunner.Runtime;

namespace XemuTestRunner.Networking;

public sealed partial class EmbeddedHttpServer
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record DiskAssetCreateRequest(
        string Id, string Kind, long Length, string Sha256, string? Description = null);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record DiskAssetImportRequest(string SourceJobId, string Path);

    private DiskAssetCatalog DiskAssets => new(_paths.Workspace);

    private async Task<bool?> TryDiskAssetRoutesAsync(
        Stream stream, HttpRequest request, CancellationToken ct)
    {
        if (request.Path == "/api/v1/help" &&
            request.Method == "GET" &&
            GetQueryValue(request.Query, "topic") == "disk-assets")
        {
            await WriteAgentJsonAsync(stream, new
            {
                capability = "diskAssets",
                catalog = "/api/v1/disk-assets",
                create = "POST /api/v1/disk-assets",
                upload = "PUT /api/v1/disk-assets/{id}/content",
                import = "POST /api/v1/disk-assets/{id}/import",
                detail = "GET /api/v1/disk-assets/{id}",
                delete = "DELETE /api/v1/disk-assets/{id}",
                runtime = "RuntimeState.DiskAssets[] pins AssetId + ExpectedSha256 + Destination; default Retention is deleteAfterEvidence.",
                snapshotBoundary = "snapshot-carrier is copied byte-for-byte; SnapshotName uses the existing -loadvm path. No extracted/overlay snapshot compatibility is claimed."
            }, cancellationToken: ct).ConfigureAwait(false);
            return false;
        }

        const string root = "/api/v1/disk-assets";
        if (request.Path != root && !request.Path.StartsWith(root + "/", StringComparison.Ordinal))
            return null;

        var catalog = DiskAssets;
        if (request.Path == root)
        {
            if (request.Method == "POST")
            {
                var body = await ReadAgentBodyAsync<DiskAssetCreateRequest>(stream, request, ct).ConfigureAwait(false);
                DiskAssetManifest createdManifest;
                try
                {
                    createdManifest = catalog.CreateOrGet(body.Id, body.Kind, body.Length, body.Sha256, body.Description, out _);
                }
                catch (InvalidOperationException error)
                {
                    throw new AgentRequestException(409, "disk_asset_conflict", error.Message,
                        "Use the existing immutable asset or choose a new asset ID.");
                }
                await WriteAgentJsonAsync(stream, DiskAssetView(catalog, createdManifest), cancellationToken: ct).ConfigureAwait(false);
                return false;
            }
            if (request.Method == "GET")
            {
                var offset = ReadBoundedQuery(request.Query, "offset", 0, 0, 1000000);
                var limit = ReadBoundedQuery(request.Query, "limit", 25, 1, 100);
                var items = catalog.List(offset, limit, out var next)
                    .Select(value => DiskAssetView(catalog, value)).ToArray();
                long stored = 0;
                foreach (var item in catalog.List(0, 100, out var more))
                {
                    if (catalog.IsReady(item)) stored = checked(stored + item.Length);
                    if (more is not null) break; // Keep catalog-list work bounded; page values remain exact.
                }
                await WriteAgentJsonAsync(stream, new
                {
                    items,
                    nextOffset = next,
                    storedBytes = stored,
                    storedBytesComplete = catalog.List(0, 100, out var extra).Count <= 100 && extra is null
                }, cancellationToken: ct).ConfigureAwait(false);
                return false;
            }
        }

        var suffix = request.Path[(root.Length + 1)..];
        var parts = suffix.Split('/', 2);
        var id = Uri.UnescapeDataString(parts[0]);
        var manifest = catalog.TryGet(id)
            ?? throw new AgentRequestException(404, "disk_asset_not_found", "No disk asset has this ID.",
                "List /api/v1/disk-assets or create the asset before referencing it.");

        if (parts.Length == 1)
        {
            if (request.Method == "GET")
            {
                await WriteAgentJsonAsync(stream, DiskAssetView(catalog, manifest), cancellationToken: ct).ConfigureAwait(false);
                return false;
            }
            if (request.Method == "DELETE")
            {
                if (!await EnsureOperationAllowedAsync(stream, "bulk_transfer", false, ct).ConfigureAwait(false))
                    return false;
                using var transfer = Activity.TrackTransfer(new { diskAsset = id, action = "delete" });
                var references = FindDiskAssetReferences(id);
                if (references.Count > 0)
                    throw new AgentRequestException(409, "disk_asset_in_use",
                        $"Disk asset '{id}' is referenced by {references.Count} retained test/job definitions.",
                        "Remove or revise those references first. The catalog will not invalidate saved tests.");
                catalog.Delete(id);
                await WriteAgentJsonAsync(stream, new { id, deleted = true }, cancellationToken: ct).ConfigureAwait(false);
                return false;
            }
        }

        if (parts.Length == 2 && parts[1] == "import" && request.Method == "POST")
        {
            if (!await EnsureOperationAllowedAsync(stream, "bulk_transfer", false, ct).ConfigureAwait(false))
                return false;
            if (catalog.IsReady(manifest))
                throw new AgentRequestException(409, "disk_asset_immutable", "This asset content is already published.",
                    "Create a new asset ID for different content.");
            var body = await ReadAgentBodyAsync<DiskAssetImportRequest>(stream, request, ct).ConfigureAwait(false);
            var sourceJob = AgentJobs.Get(body.SourceJobId);
            if (sourceJob.State is not ("draft" or "tested" or "cancelled"))
                throw new AgentRequestException(409, "disk_asset_source_busy",
                    "Disk import source must be a stable draft, tested, or cancelled API package.",
                    "Wait for the source attempt to leave queued/testing ownership.");
            await using var source = AgentJobs.OpenFile(body.SourceJobId, body.Path);
            if (source.Length != manifest.Length)
                throw new AgentRequestException(409, "disk_asset_source_mismatch",
                    "Source file length does not match the catalog definition.",
                    "Use the file declaration's exact length and SHA-256.");
            using var transfer = Activity.TrackTransfer(new
            {
                diskAsset = id,
                action = "import",
                sourceJob = body.SourceJobId
            });
            var receipt = await _uploads.ReceiveAsync(source, catalog.ContentPath(id), source.Length, null,
                manifest.Sha256, null, _options.TransferBufferBytes, ct).ConfigureAwait(false);
            if (!receipt.Complete)
                throw new InvalidDataException("Local disk import did not publish atomically.");
            await WriteAgentJsonAsync(stream, DiskAssetView(catalog, manifest), cancellationToken: ct).ConfigureAwait(false);
            return false;
        }

        if (parts.Length == 2 && parts[1] == "content")
        {
            var target = catalog.ContentPath(id);
            if (request.Method == "GET" && QueryContains(request.Query, "upload-status", "1"))
            {
                var status = _uploads.GetStatus(target);
                await WriteAgentJsonAsync(stream, new
                {
                    complete = status.Complete,
                    partial = status.Partial,
                    length = status.Length,
                    total = status.Total ?? manifest.Length,
                    uploadId = status.UploadId,
                    state = status.State,
                    sha256 = status.Sha256 ?? manifest.Sha256
                }, cancellationToken: ct).ConfigureAwait(false);
                return false;
            }

            if (request.Method is "PUT" or "POST")
            {
                if (!await EnsureOperationAllowedAsync(stream, "bulk_transfer", false, ct).ConfigureAwait(false))
                    return false;
                if (catalog.IsReady(manifest))
                    throw new AgentRequestException(409, "disk_asset_immutable", "This asset content is already published.",
                        "Create a new asset ID for different content.");
                var upload = ParseUploadRequest(request);
                var total = upload.Range?.Total ?? upload.Length;
                if (total != manifest.Length)
                    throw new AgentRequestException(409, "disk_asset_length_mismatch",
                        $"Upload declares {total} bytes but asset '{id}' requires {manifest.Length}.",
                        "Use the immutable manifest length and resume its existing upload.");
                if (upload.ExpectedHash is not null &&
                    !upload.ExpectedHash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new AgentRequestException(409, "disk_asset_hash_mismatch",
                        "Upload digest differs from the immutable disk asset digest.",
                        "Use the manifest SHA-256 or create a different asset ID.");
                using var transfer = Activity.TrackTransfer(new { diskAsset = id, action = "upload" });
                var receipt = await _uploads.ReceiveAsync(stream, target, upload.Length, upload.Range,
                    manifest.Sha256, upload.Id, _options.TransferBufferBytes, ct).ConfigureAwait(false);
                await WriteAgentJsonAsync(stream, new
                {
                    id,
                    complete = receipt.Complete,
                    received = receipt.Received,
                    total = receipt.Total,
                    uploadId = receipt.UploadId,
                    sha256 = receipt.Sha256 ?? manifest.Sha256
                }, receipt.Complete ? 201 : 202, cancellationToken: ct).ConfigureAwait(false);
                return false;
            }

        }

        throw new AgentRequestException(404, "route_not_found", "No matching disk asset action.",
            "GET /api/v1/help?topic=disk-assets for the supported catalog workflow.");
    }

    private object DiskAssetView(DiskAssetCatalog catalog, DiskAssetManifest manifest)
    {
        var status = _uploads.GetStatus(catalog.ContentPath(manifest.Id));
        var ready = status.Complete && !status.Partial && status.Length == manifest.Length;
        return new
        {
            id = manifest.Id,
            kind = manifest.Kind,
            length = manifest.Length,
            sha256 = manifest.Sha256,
            manifest.Description,
            createdUtc = manifest.CreatedUtc,
            ready,
            uploadState = status.State,
            upload = $"/api/v1/disk-assets/{Uri.EscapeDataString(manifest.Id)}/content"
        };
    }

    private IReadOnlyList<string> FindDiskAssetReferences(string id)
    {
        var references = new List<string>();
        var scanned = 0;
        foreach (var root in new[] { _paths.Pending, _paths.Testing, _paths.Tested })
        {
            if (!Directory.Exists(root)) continue;
            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((root, 0));
            while (pending.Count > 0)
            {
                var item = pending.Pop();
                RunStateInventory.NoLinks(item.Path);
                foreach (var entry in Directory.EnumerateFileSystemEntries(item.Path))
                {
                    if (++scanned > 20000)
                        throw new InvalidDataException("Disk asset reference scan exceeded 20000 filesystem entries.");
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Linked queue paths block safe disk asset deletion.");
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (item.Depth >= 12)
                            throw new InvalidDataException("Disk asset reference scan exceeded directory depth 12.");
                        pending.Push((entry, item.Depth + 1));
                        continue;
                    }
                    if (!entry.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                    var info = new FileInfo(entry);
                    if (info.Length > 1024 * 1024)
                        throw new InvalidDataException("A queue JSON file exceeds the bounded reference scan size.");
                    try
                    {
                        using var document = JsonDocument.Parse(File.ReadAllBytes(entry));
                        if (ContainsAssetReference(document.RootElement, id))
                            references.Add(Path.GetRelativePath(_paths.Workspace, entry).Replace('\\', '/'));
                    }
                    catch (JsonException)
                    {
                        // A partially written JSON file means deletion cannot be proven safe.
                        throw new InvalidDataException("Unreadable queue metadata blocks disk asset deletion.");
                    }
                }
            }
        }
        return references;
    }

    private static bool ContainsAssetReference(JsonElement element, string id)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals("assetId", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    property.Value.GetString() == id)
                    return true;
                if (ContainsAssetReference(property.Value, id)) return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in element.EnumerateArray())
                if (ContainsAssetReference(value, id)) return true;
        }
        return false;
    }
}
