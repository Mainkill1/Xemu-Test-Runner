using System.Security.Cryptography;
using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Networking;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Runtime;

public sealed record LocalDiskImportReceipt(string Id, string Path, string? Description, string State,
    DateTimeOffset CreatedUtc, long TotalBytes, long CopiedBytes = 0, string? Sha256 = null, string? Error = null);

public sealed class LocalDiskImportStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CancellationTokenSource> _running = [];
    private readonly HashSet<string> _cancelled = [];
    private readonly string _workspace;
    private readonly string _root;
    private readonly HttpOptions _options;
    private readonly ActivityHub _activity;
    public LocalDiskImportStore(string workspace, HttpOptions options, ActivityHub activity)
    {
        _workspace = Path.GetFullPath(workspace);
        _root = Path.Combine(_workspace, ".disk-imports");
        _options = options;
        _activity = activity;
    }
    internal object OwnershipGate => _gate;
    public bool HasPendingOperations { get { lock (_gate) return _running.Count != 0; } }
    internal bool HasDefinition(string id) { lock (_gate) return Read(id) is not null; }
    public bool IsRunning(string id) { lock (_gate) return _running.ContainsKey(id); }
    public LocalDiskImportReceipt? Get(string id)
    {
        lock (_gate)
        {
            var receipt = Read(id);
            if (receipt?.State is "queued" or "running" or "cancelling" or "publishing" && !_running.ContainsKey(id))
                return receipt with { State = "interrupted", Error = "Runner restarted before import completed. Retry the same ID/path to acquire a fresh verified copy." };
            return receipt;
        }
    }
    public LocalDiskImportReceipt Cancel(string id)
    {
        lock (_gate)
        {
            var receipt = Read(id) ?? throw new AgentRequestException(404, "disk_import_not_found", "No local import has this ID.", "Inspect import-local status.");
            if (_running.TryGetValue(id, out var stop) && receipt.State != "completed")
            {
                _cancelled.Add(id);
                receipt = receipt with { State = "cancelling" };
                Write(receipt);
                stop.Cancel();
            }
            return receipt;
        }
    }
    public LocalDiskImportReceipt Start(string id, string sourcePath, string? description, CancellationToken lifetime)
    {
        if (!DiskAssetCatalog.IsValidId(id)) throw new InvalidDataException("Invalid disk asset ID.");
        sourcePath = ValidateSourcePath(sourcePath);
        if (description is { Length: > 240 } || description?.Any(char.IsControl) == true)
            throw new InvalidDataException("Description must contain at most 240 non-control characters.");
        lock (_gate)
        {
            var existing = Read(id);
            if (existing is not null)
            {
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!existing.Path.Equals(sourcePath, comparison) || existing.Description != description)
                    throw new AgentRequestException(409, "disk_asset_conflict", "This import ID belongs to another source or description.", "Use the original import definition or a new ID.");
                if (_running.ContainsKey(id) || existing.State == "completed") return existing;
            }
            var catalog = new DiskAssetCatalog(_workspace);
            if (catalog.TryGet(id) is { } registered && (existing is null || catalog.IsReady(registered)))
                throw new AgentRequestException(409, "disk_asset_conflict", "This disk asset ID is already registered.", "Use a new ID for a local import.");
            // On Windows, FileShare.Read denies a concurrent writer/deleter.
            // Keep this exact handle for validation and the entire owned copy.
            var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                _options.TransferBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            IDisposable? transfer = null;
            try
            {
                RunStateInventory.NoLinks(sourcePath);
                if (source.Length is < 72 or > (1L << 44)) throw new InvalidDataException("Carrier size is outside supported disk asset bounds.");
                _ = QcowSnapshotDirectory.Read(source);
                source.Position = 0;
                RunStateInventory.NoLinks(_root);
                Directory.CreateDirectory(_root);
                var receipt = new LocalDiskImportReceipt(id, sourcePath, description, "queued", DateTimeOffset.UtcNow, source.Length);
                AtomicJson.Write(ReceiptPath(id), receipt);
                transfer = _activity.TrackTransfer(new { diskAsset = id, action = "import-local" });
                var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                _running.Add(id, stop);
                var ownedTransfer = transfer;
                var sourceTime = File.GetLastWriteTimeUtc(sourcePath);
                _ = Task.Run(() => CopyAsync(receipt, source, sourceTime, ownedTransfer, stop.Token), CancellationToken.None);
                return receipt;
            }
            catch { source.Dispose(); transfer?.Dispose(); if (_running.Remove(id, out var stop)) stop.Dispose(); throw; }
        }
    }
    private async Task CopyAsync(LocalDiskImportReceipt receipt, FileStream source, DateTime sourceTime, IDisposable transfer, CancellationToken ct)
    {
        var stage = Path.Combine(_root, receipt.Id + ".partial");
        try
        {
            await using (source.ConfigureAwait(false))
            {
                RunStateInventory.NoLinks(stage);
                Write(receipt with { State = "running" });
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[_options.TransferBufferBytes];
                long copied = 0;
                long nextReceipt = 64 * 1024 * 1024;
                await using (var output = new FileStream(stage, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.Asynchronous))
                {
                    for (int count; (count = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0;)
                    {
                        copied = checked(copied + count);
                        if (copied > receipt.TotalBytes) throw new InvalidDataException("Source grew during import.");
                        digest.AppendData(buffer, 0, count);
                        await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                        if (copied >= nextReceipt)
                        {
                            Write(receipt with { State = "running", CopiedBytes = copied });
                            nextReceipt = copied + 64 * 1024 * 1024;
                        }
                    }
                    await output.FlushAsync(ct).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }
                if (copied != receipt.TotalBytes || source.Length != receipt.TotalBytes || File.GetLastWriteTimeUtc(receipt.Path) != sourceTime)
                    throw new InvalidDataException("Source changed during import; no carrier was published.");
                RunStateInventory.NoLinks(receipt.Path);
                var hash = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
                // Independently verify retained bytes before catalog publication.
                await using (var check = new FileStream(stage, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.Asynchronous))
                {
                    var retainedHash = Convert.ToHexString(await SHA256.HashDataAsync(check, ct).ConfigureAwait(false)).ToLowerInvariant();
                    if (check.Length != receipt.TotalBytes || retainedHash != hash) throw new InvalidDataException("Retained carrier verification failed.");
                    _ = QcowSnapshotDirectory.Read(check);
                }
                ct.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    ct.ThrowIfCancellationRequested();
                    receipt = receipt with { State = "publishing", CopiedBytes = copied, Sha256 = hash };
                    Write(receipt);
                    var catalog = new DiskAssetCatalog(_workspace);
                    _ = catalog.CreateOrGet(receipt.Id, "snapshot-carrier", receipt.TotalBytes, hash, receipt.Description, out _);
                    File.Move(stage, catalog.ContentPath(receipt.Id), overwrite: false);
                    // Publication is authoritative even if shutdown arrives now.
                    Write(receipt with { State = "completed", CopiedBytes = copied, Sha256 = hash });
                }
            }
        }
        catch (Exception error)
        {
            try
            {
                // A crash after rename can be reconciled by Get() on restart.
                var asset = new DiskAssetCatalog(_workspace).TryGet(receipt.Id);
                if (asset is not null && MatchesPublication(receipt, asset) && new DiskAssetCatalog(_workspace).IsReady(asset))
                    Write(receipt with { State = "completed", CopiedBytes = asset.Length, Sha256 = asset.Sha256 });
                else Write(receipt with { State = error is OperationCanceledException ? (WasCancelled(receipt.Id) ? "cancelled" : "interrupted") : "failed", Error = error.Message });
            }
            catch (Exception writeError) { System.Diagnostics.Trace.TraceError("Cannot persist disk import outcome: {0}", writeError); }
        }
        finally
        {
            source.Dispose(); transfer.Dispose();
            try { if (File.Exists(stage)) File.Delete(stage); }
            catch (IOException error) { System.Diagnostics.Trace.TraceError("Cannot remove import staging: {0}", error); }
            lock (_gate) { _cancelled.Remove(receipt.Id); if (_running.Remove(receipt.Id, out var stop)) stop.Dispose(); }
        }
    }
    private string ValidateSourcePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl))
            throw new InvalidDataException("Import requires an absolute local QCOW2 path.");
        if (OperatingSystem.IsWindows() && value.Length > 3 && value[0] == '/' && char.IsAsciiLetter(value[1]) && value[2] == ':') value = value[1..];
        if (!Path.IsPathFullyQualified(value) || value.StartsWith(@"\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidDataException("Network/device or relative import paths are not supported.");
        var path = Path.GetFullPath(value);
        if (!path.EndsWith(".qcow2", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Local import accepts only .qcow2 snapshot carriers.");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var roots = new[] { _workspace }.Concat(_options.LocalDiskImportRoots);
        if (!roots.Any(root => path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, comparison)))
            throw new InvalidDataException("Path is outside the workspace and configured Http.LocalDiskImportRoots.");
        RunStateInventory.NoLinks(path);
        return path;
    }
    private string ReceiptPath(string id)
    {
        if (!DiskAssetCatalog.IsValidId(id)) throw new InvalidDataException("Invalid disk asset ID.");
        var path = Path.Combine(_root, id + ".json"); RunStateInventory.NoLinks(path); return path;
    }
    private LocalDiskImportReceipt? Read(string id)
    {
        var path = ReceiptPath(id);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("Import receipt exceeds 64 KiB.");
        var receipt = JsonSerializer.Deserialize<LocalDiskImportReceipt>(File.ReadAllText(path), ConfigLoader.JsonOptions)
            ?? throw new InvalidDataException("Import receipt is empty.");
        if (receipt.State != "completed")
        {
            var catalog = new DiskAssetCatalog(_workspace);
            var asset = catalog.TryGet(id);
            if (asset is not null && MatchesPublication(receipt, asset) && catalog.IsReady(asset))
                receipt = receipt with { State = "completed", CopiedBytes = asset.Length, Sha256 = asset.Sha256, Error = null };
        }
        return receipt;
    }
    private static bool MatchesPublication(LocalDiskImportReceipt receipt, DiskAssetManifest asset) =>
        receipt.Sha256 is not null && asset.Id == receipt.Id && asset.Kind == "snapshot-carrier" &&
        asset.Length == receipt.TotalBytes && asset.Sha256.Equals(receipt.Sha256, StringComparison.OrdinalIgnoreCase) &&
        asset.Description == receipt.Description;
    private bool WasCancelled(string id) { lock (_gate) return _cancelled.Contains(id); }
    private void Write(LocalDiskImportReceipt receipt)
    {
        lock (_gate)
        {
            if (receipt.State == "running" && _cancelled.Contains(receipt.Id)) receipt = receipt with { State = "cancelling" };
            AtomicJson.Write(ReceiptPath(receipt.Id), receipt);
        }
    }
}
