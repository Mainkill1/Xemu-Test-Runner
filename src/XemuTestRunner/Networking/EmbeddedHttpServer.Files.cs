using System.Globalization;
using XemuTestRunner.Reliability;
using XemuTestRunner.Util;

namespace XemuTestRunner.Networking;

public sealed partial class EmbeddedHttpServer
{
    private readonly FileUploadStore _uploads = new();

    private async Task HandleFileReadAsync(
        Stream stream, HttpRequest request, string relative, bool keepAlive, CancellationToken cancellationToken)
    {
        string path;
        try
        {
            path = ResolveTransferPath(relative);
        }
        catch (Exception exception) when (exception is InvalidDataException or UnauthorizedAccessException)
        {
            await WriteApiErrorAsync(stream, 400, "Bad Request", "file_path_invalid", exception.Message,
                "Use a relative path under the file root, not a linked path or the internal upload staging directory.",
                keepAlive, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (QueryContains(request.Query, "upload-status", "1"))
        {
            try
            {
                var status = _uploads.GetStatus(path);
                if (request.Method == "HEAD")
                {
                    await WriteEmptyAsync(stream, 200, "OK", keepAlive, cancellationToken).ConfigureAwait(false);
                    return;
                }

                await WriteJsonAsync(stream, 200, "OK", new
                {
                    path = relative,
                    complete = status.Complete,
                    partial = status.Partial,
                    length = status.Length,
                    total = status.Total,
                    uploadId = status.UploadId,
                    completedFileBytes = status.CompletedFileBytes,
                    state = status.State,
                    sha256 = status.Sha256
                }, keepAlive, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await WriteApiErrorAsync(stream, 409, "Conflict", "upload_state_invalid", exception.Message,
                    "Restart with a whole-file upload. Existing published data is not modified by a failed status query.",
                    false, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        // Open before publishing response headers. A reader keeps this exact file
        // handle even if another request publishes a newer version of the path.
        FileStream file;
        try
        {
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                _options.TransferBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var missing = exception is FileNotFoundException or DirectoryNotFoundException;
            await WriteApiErrorAsync(stream, missing ? 404 : 409, missing ? "Not Found" : "Conflict",
                missing ? "file_not_found" : "file_unavailable", exception.Message,
                "Check the relative path, file permissions, and ?upload-status=1 before retrying.",
                false, cancellationToken).ConfigureAwait(false);
            return;
        }

        await using (file)
        {
            FileRange range;
            try
            {
                range = FileRange.Parse(request.Headers.GetValueOrDefault("Range"), file.Length);
            }
            catch (InvalidDataException)
            {
                await WriteHeadersAsync(stream, 416, "Range Not Satisfiable", new Dictionary<string, string>
                {
                    ["Content-Range"] = $"bytes */{file.Length.ToString(CultureInfo.InvariantCulture)}",
                    ["Content-Length"] = "0"
                }, keepAlive, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var headers = new Dictionary<string, string>
            {
                ["Content-Type"] = "application/octet-stream",
                ["Content-Length"] = range.Length.ToString(CultureInfo.InvariantCulture),
                ["Accept-Ranges"] = "bytes",
                ["Content-Disposition"] = $"attachment; filename=\"{EscapeHeaderValue(Path.GetFileName(path))}\""
            };
            if (range.Partial)
            {
                headers["Content-Range"] = $"bytes {range.Start}-{range.Start + range.Length - 1}/{file.Length}";
            }

            await WriteHeadersAsync(stream, range.Partial ? 206 : 200,
                range.Partial ? "Partial Content" : "OK", headers, keepAlive, cancellationToken).ConfigureAwait(false);
            if (request.Method != "HEAD" && range.Length > 0)
            {
                file.Position = range.Start;
                await CopyBytesAsync(file, stream, range.Length, cancellationToken).ConfigureAwait(false);
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleFileUploadAsync(
        Stream stream, HttpRequest request, string relative, bool keepAlive, CancellationToken cancellationToken)
    {
        try
        {
            var target = ResolveTransferPath(relative);
            var upload = ParseUploadRequest(request);
            var receipt = await _uploads.ReceiveAsync(
                stream, target, upload.Length, upload.Range, upload.ExpectedHash, upload.Id,
                _options.TransferBufferBytes, cancellationToken).ConfigureAwait(false);

            await WriteJsonAsync(stream, receipt.Complete ? 201 : 202,
                receipt.Complete ? "Created" : "Accepted", new
                {
                    path = relative,
                    complete = receipt.Complete,
                    bytes = receipt.Received,
                    received = receipt.Received,
                    total = receipt.Total,
                    uploadId = receipt.UploadId,
                    sha256 = receipt.Sha256
                }, keepAlive, cancellationToken).ConfigureAwait(false);
        }
        catch (UploadFailure failure)
        {
            var reason = failure.Status switch
            {
                400 => "Bad Request",
                411 => "Length Required",
                422 => "Unprocessable Content",
                _ => "Conflict"
            };
            await WriteApiErrorAsync(stream, failure.Status, reason, failure.Code, failure.Message,
                failure.Hint, false, cancellationToken, failure.Details).ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            await WriteApiErrorAsync(stream, 400, "Bad Request", "file_path_or_state_invalid", exception.Message,
                "Use a relative file path. An unreadable resumable session can be replaced with a whole-file upload.",
                false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await WriteApiErrorAsync(stream, 409, "Conflict", "upload_io_error", exception.Message,
                "Check disk space, permissions, and upload status. A failed transfer does not replace the published file.",
                false, cancellationToken).ConfigureAwait(false);
        }
    }

    private string ResolveTransferPath(string relative)
    {
        var path = PathGuard.ResolveFile(_paths.FileRoot, relative);
        var components = Path.GetRelativePath(_paths.FileRoot, path).Split(Path.DirectorySeparatorChar);
        if (components.Any(part => part.Equals(FileUploadStore.StagingDirectoryName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("The internal upload staging directory is not an API file path.");
        }

        return path;
    }

    private static UploadRequest ParseUploadRequest(HttpRequest request)
    {
        if (request.ContentLength is null)
        {
            throw new UploadFailure(411, "content_length_required", "Content-Length is required for uploads.",
                "Send the exact body byte count. Chunked request encoding is not supported.");
        }

        var length = request.ContentLength.Value;
        if (length < 0)
        {
            throw new UploadFailure(400, "content_length_invalid", "Content-Length cannot be negative.",
                "Send a non-negative decimal byte count.");
        }

        var hash = request.Headers.GetValueOrDefault("X-Content-SHA256")?.Trim().ToLowerInvariant();
        if (hash is not null && (hash.Length != 64 || !hash.All(Uri.IsHexDigit)))
        {
            throw new UploadFailure(400, "content_hash_invalid", "X-Content-SHA256 must contain 64 hexadecimal characters.",
                "Use the SHA-256 digest of the complete final file, not the current chunk.");
        }

        var id = request.Headers.GetValueOrDefault("X-Upload-Id");
        if (id is not null && !Guid.TryParseExact(id, "N", out _))
        {
            throw new UploadFailure(400, "upload_identity_invalid", "X-Upload-Id is invalid.",
                "Copy uploadId from the initial 202 response or ?upload-status=1.");
        }

        UploadRange? range = null;
        if (request.Headers.TryGetValue("Content-Range", out var value))
        {
            range = ParseUploadRange(value);
            if (range is null || range.End - range.Start + 1 != length)
            {
                throw new UploadFailure(400, "content_range_invalid", "Content-Range does not match Content-Length.",
                    "Use 'bytes start-end/total' for the next sequential chunk.");
            }
        }

        return new UploadRequest(length, range, hash, id);
    }

    private static UploadRange? ParseUploadRange(string value)
    {
        if (!value.StartsWith("bytes ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var pieces = value[6..].Split(['-', '/']);
        if (pieces.Length != 3 ||
            !long.TryParse(pieces[0], NumberStyles.None, CultureInfo.InvariantCulture, out var start) ||
            !long.TryParse(pieces[1], NumberStyles.None, CultureInfo.InvariantCulture, out var end) ||
            !long.TryParse(pieces[2], NumberStyles.None, CultureInfo.InvariantCulture, out var total) ||
            start < 0 || end < start || total <= end)
        {
            return null;
        }

        return new UploadRange(start, end, total);
    }

    private static bool QueryContains(string query, string key, string value)
    {
        foreach (var item in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            if (pair.Length == 2 && Uri.UnescapeDataString(pair[0]) == key && Uri.UnescapeDataString(pair[1]) == value)
            {
                return true;
            }
        }

        return false;
    }

    private sealed record UploadRequest(long Length, UploadRange? Range, string? ExpectedHash, string? Id);
}
