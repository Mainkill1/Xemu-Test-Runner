using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Networking;

internal sealed record UploadRange(long Start, long End, long Total);
internal sealed record UploadReceipt(bool Complete, long Received, long Total, string? UploadId, string? Sha256);
internal sealed record UploadStatus(
    bool Complete, bool Partial, long Length, long? Total, string? UploadId,
    long? CompletedFileBytes, string State, string? Sha256);

internal sealed class UploadFailure(
    int status, string code, string message, string hint, object? details = null) : IOException(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string Hint { get; } = hint;
    public object? Details { get; } = details;
}

/// <summary>
/// Receives files into private staging. Only verified, complete files replace a
/// published destination. HTTP parsing and error responses belong to the server.
/// </summary>
internal sealed class FileUploadStore
{
    public const string StagingDirectoryName = ".runner-uploads";
    private readonly object _gate = new();
    private readonly HashSet<string> _activeDestinations = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public UploadStatus GetStatus(string target)
    {
        var paths = GetStagingPaths(target);
        var state = ReadState(paths.State);
        long? publishedBytes = File.Exists(target) ? new FileInfo(target).Length : null;
        if (state is not null)
        {
            var partialExists = File.Exists(paths.Partial);
            return new UploadStatus(false, true, state.AcceptedBytes, state.TotalBytes,
                state.Id, publishedBytes, partialExists ? "receiving" : "publication_unconfirmed", state.Sha256);
        }

        return new UploadStatus(publishedBytes is not null, false, publishedBytes ?? 0,
            publishedBytes, null, publishedBytes, publishedBytes is null ? "missing" : "complete", null);
    }

    public async Task<UploadReceipt> ReceiveAsync(
        Stream body, string target, long contentLength, UploadRange? range,
        string? expectedSha256, string? uploadId, int bufferBytes, CancellationToken cancellationToken)
    {
        using var ownership = Acquire(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var paths = GetStagingPaths(target);
        Directory.CreateDirectory(paths.Directory);

        if (range is null)
        {
            return await ReceiveWholeFileAsync(
                body, target, paths, contentLength, expectedSha256, bufferBytes, cancellationToken)
                .ConfigureAwait(false);
        }

        return await ReceiveChunkAsync(
            body, target, paths, contentLength, range, expectedSha256, uploadId,
            bufferBytes, cancellationToken).ConfigureAwait(false);
    }

    private IDisposable Acquire(string target)
    {
        lock (_gate)
        {
            if (!_activeDestinations.Add(target))
            {
                throw new UploadFailure(409, "upload_in_progress",
                    "Another upload is already writing this destination.",
                    "Wait for it to finish or choose a different destination.");
            }
        }

        // Removing a reservation is one atomic operation. There are no detached
        // semaphores that a second request could acquire while a third replaces it.
        return new DestinationLease(this, target);
    }

    private async Task<UploadReceipt> ReceiveWholeFileAsync(
        Stream body, string target, StagingPaths paths, long length, string? expectedHash,
        int bufferBytes, CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(paths.Directory, Guid.NewGuid().ToString("N") + ".upload");
        try
        {
            await using (var file = OpenOutput(temporary, FileMode.CreateNew, bufferBytes))
            {
                await CopyExactlyAsync(body, file, length, bufferBytes, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var actualHash = await VerifyAndPublishAsync(
                temporary, target, expectedHash, cancellationToken).ConfigureAwait(false);

            // A successful whole-file upload explicitly replaces any abandoned
            // resumable session. A failed upload leaves both old states untouched.
            DeleteIfPresent(paths.Partial);
            DeleteIfPresent(paths.State);
            return new UploadReceipt(true, length, length, null, actualHash);
        }
        finally
        {
            DeleteIfPresent(temporary);
        }
    }

    private async Task<UploadReceipt> ReceiveChunkAsync(
        Stream body, string target, StagingPaths paths, long length, UploadRange range,
        string? expectedHash, string? uploadId, int bufferBytes, CancellationToken cancellationToken)
    {
        var state = ReadState(paths.State);
        if (state is null)
        {
            if (range.Start != 0)
            {
                throw OffsetMismatch(0);
            }

            state = new UploadState(Guid.NewGuid().ToString("N"), range.Total, 0, expectedHash);
            AtomicJson.Write(paths.State, state);
        }
        else
        {
            var sameId = string.Equals(uploadId, state.Id, StringComparison.Ordinal);
            var sameHash = state.Sha256 is not null && expectedHash is not null &&
                state.Sha256.Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
            if ((!sameId && !sameHash) || state.TotalBytes != range.Total ||
                (expectedHash is not null && state.Sha256 is not null && !sameHash))
            {
                throw new UploadFailure(409, "upload_identity_mismatch",
                    "This chunk does not identify the existing upload session.",
                    "Query ?upload-status=1 and send its uploadId as X-Upload-Id. Keep the total size and whole-file digest unchanged.");
            }

            // A digest may be added later, but never changed once one is pinned.
            if (state.Sha256 is null && expectedHash is not null)
            {
                state = state with { Sha256 = expectedHash };
                AtomicJson.Write(paths.State, state);
            }
        }

        if (range.Start != state.AcceptedBytes)
        {
            throw OffsetMismatch(state.AcceptedBytes);
        }

        await using (var file = OpenOutput(paths.Partial, FileMode.OpenOrCreate, bufferBytes))
        {
            if (file.Length < state.AcceptedBytes)
            {
                throw new UploadFailure(409, "upload_state_invalid",
                    "The partial file is shorter than its committed upload offset.",
                    "Restart with a whole-file upload. The existing published file has not been changed.");
            }

            // Bytes written before a crash but not acknowledged in the manifest
            // are discarded. Resume always starts at the committed offset.
            file.SetLength(state.AcceptedBytes);
            file.Position = state.AcceptedBytes;
            try
            {
                await CopyExactlyAsync(body, file, length, bufferBytes, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                file.SetLength(state.AcceptedBytes);
                throw;
            }
        }

        state = state with { AcceptedBytes = range.End + 1 };
        AtomicJson.Write(paths.State, state);
        if (state.AcceptedBytes != state.TotalBytes)
        {
            return new UploadReceipt(false, state.AcceptedBytes, state.TotalBytes, state.Id, null);
        }

        string? actualHash;
        try
        {
            actualHash = await VerifyAndPublishAsync(
                paths.Partial, target, state.Sha256, cancellationToken).ConfigureAwait(false);
        }
        catch (UploadFailure failure) when (failure.Code == "upload_hash_mismatch")
        {
            DeleteIfPresent(paths.Partial);
            DeleteIfPresent(paths.State);
            throw;
        }

        DeleteIfPresent(paths.State);
        return new UploadReceipt(true, state.AcceptedBytes, state.TotalBytes, state.Id, actualHash);
    }

    private static async Task<string?> VerifyAndPublishAsync(
        string temporary, string target, string? expectedHash, CancellationToken cancellationToken)
    {
        string? actualHash = null;
        if (expectedHash is not null)
        {
            await using var file = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            actualHash = Convert.ToHexString(
                await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new UploadFailure(422, "upload_hash_mismatch",
                    "Uploaded file SHA-256 does not match X-Content-SHA256.",
                    "Verify the source digest and restart the upload. The previous published file is unchanged.",
                    new { expectedSha256 = expectedHash, actualSha256 = actualHash });
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        File.Move(temporary, target, overwrite: true);
        return actualHash;
    }

    private static FileStream OpenOutput(string path, FileMode mode, int bufferBytes) =>
        new(path, mode, FileAccess.Write, FileShare.Read, bufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task CopyExactlyAsync(
        Stream source, Stream destination, long length, int bufferBytes, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(bufferBytes);
        try
        {
            var remaining = length;
            while (remaining > 0)
            {
                var count = (int)Math.Min(remaining, buffer.Length);
                var read = await source.ReadAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException($"Upload ended with {remaining} bytes still expected.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static UploadState? ReadState(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        if (new FileInfo(path).Length > 64 * 1024)
        {
            throw new InvalidDataException("Upload state exceeds 64 KiB.");
        }

        try
        {
            var state = JsonSerializer.Deserialize<UploadState>(File.ReadAllText(path), ConfigLoader.JsonOptions);
            if (state is null || !Guid.TryParseExact(state.Id, "N", out _) ||
                state.TotalBytes <= 0 || state.AcceptedBytes < 0 || state.AcceptedBytes > state.TotalBytes ||
                (state.Sha256 is not null && (state.Sha256.Length != 64 || !state.Sha256.All(Uri.IsHexDigit))))
            {
                throw new InvalidDataException("Upload state is invalid.");
            }

            return state;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Upload state is unreadable.", exception);
        }
    }

    private static StagingPaths GetStagingPaths(string target)
    {
        var identity = OperatingSystem.IsWindows() ? target.ToUpperInvariant() : target;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var directory = Path.Combine(Path.GetDirectoryName(target)!, StagingDirectoryName, key);
        return new StagingPaths(directory, Path.Combine(directory, "incoming.part"), Path.Combine(directory, "state.json"));
    }

    private static UploadFailure OffsetMismatch(long expectedOffset) => new(409, "upload_offset_mismatch",
        $"The server expects byte offset {expectedOffset}.",
        "Query ?upload-status=1, then resume from its committed length using X-Upload-Id.",
        new { expectedOffset });

    private static void DeleteIfPresent(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Cleanup is not allowed to undo an already-verified publication.
        }
        catch (UnauthorizedAccessException)
        {
            // The next request will report any inaccessible leftover state.
        }
    }

    private sealed record StagingPaths(string Directory, string Partial, string State);
    private sealed record UploadState(string Id, long TotalBytes, long AcceptedBytes, string? Sha256);

    private sealed class DestinationLease(FileUploadStore owner, string target) : IDisposable
    {
        private FileUploadStore? _owner = owner;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null)
            {
                return;
            }

            lock (current._gate)
            {
                current._activeDestinations.Remove(target);
            }
        }
    }
}
