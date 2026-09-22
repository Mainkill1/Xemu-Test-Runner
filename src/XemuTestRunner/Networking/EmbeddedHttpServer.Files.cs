using System.Collections.Concurrent;
using System.Security.Cryptography;
using XemuTestRunner.Util;

namespace XemuTestRunner.Networking;

public sealed partial class EmbeddedHttpServer
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim>
        _uploadLocks = new(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
    private async Task HandleFileReadAsync(Stream stream, HttpRequest request, string relative, bool keepAlive, CancellationToken cancellationToken)
    {
        string path;
        try { path = PathGuard.ResolveFile(_paths.FileRoot, relative); }
        catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException)
        {
            await WriteJsonAsync(stream, 400, "Bad Request", new { error = ex.Message }, keepAlive, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (QueryContains(request.Query, "upload-status", "1"))
        {
            var partial = path + ".partial";
            var completeExists = File.Exists(path);
            var partialExists = File.Exists(partial);
            var uploadedLength = completeExists ? new FileInfo(path).Length : partialExists ? new FileInfo(partial).Length : 0;
            await WriteJsonAsync(stream, 200, "OK", new
            {
                path = relative,
                complete = completeExists,
                partial = partialExists,
                length = uploadedLength
            }, keepAlive, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!File.Exists(path))
        {
            await WriteJsonAsync(stream, 404, "Not Found", new { error = "File not found." }, keepAlive, cancellationToken).ConfigureAwait(false);
            return;
        }

        var fileInfo = new FileInfo(path);
        var start = 0L;
        var end = fileInfo.Length - 1;
        var partialResponse = false;

        if (request.Headers.TryGetValue("Range", out var range) && TryParseRange(range, fileInfo.Length, out var parsedStart, out var parsedEnd))
        {
            start = parsedStart;
            end = parsedEnd;
            partialResponse = true;
        }

        var length = fileInfo.Length == 0 ? 0 : end - start + 1;
        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/octet-stream",
            ["Content-Length"] = length.ToString(),
            ["Accept-Ranges"] = "bytes",
            ["Content-Disposition"] = $"attachment; filename=\"{EscapeHeaderValue(fileInfo.Name)}\""
        };
        if (partialResponse)
            headers["Content-Range"] = $"bytes {start}-{end}/{fileInfo.Length}";

        await WriteHeadersAsync(stream, partialResponse ? 206 : 200, partialResponse ? "Partial Content" : "OK", headers, keepAlive, cancellationToken).ConfigureAwait(false);

        if (request.Method == "HEAD" || length == 0)
        {
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, _options.TransferBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        file.Seek(start, SeekOrigin.Begin);
        await CopyBytesAsync(file, stream, length, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleFileUploadAsync(
        Stream stream,
        HttpRequest request,
        string relative,
        bool keepAlive,
        CancellationToken cancellationToken)
    {
        string target;
        try
        {
            target = PathGuard.ResolveFile(
                _paths.FileRoot,
                relative);
        }
        catch (Exception ex) when (
            ex is InvalidDataException or
            UnauthorizedAccessException)
        {
            await WriteJsonAsync(
                stream,
                400,
                "Bad Request",
                new { error = ex.Message },
                false,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var gate = _uploadLocks.GetOrAdd(
            target,
            static _ => new SemaphoreSlim(1, 1));

        if (!await gate.WaitAsync(
                0,
                cancellationToken).ConfigureAwait(false))
        {
            await WriteJsonAsync(
                stream,
                409,
                "Conflict",
                new
                {
                    error =
                        "Another upload is already writing this destination.",
                    path = relative
                },
                false,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await HandleFileUploadCoreAsync(
                stream,
                request,
                relative,
                target,
                keepAlive,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
            if (gate.CurrentCount == 1)
                _uploadLocks.TryRemove(
                    new KeyValuePair<string, SemaphoreSlim>(
                        target,
                        gate));
        }
    }

    private async Task HandleFileUploadCoreAsync(
        Stream stream,
        HttpRequest request,
        string relative,
        string target,
        bool keepAlive,
        CancellationToken cancellationToken)
    {
        var contentLength = request.ContentLength;
        if (contentLength is null)
        {
            await WriteJsonAsync(stream, 411, "Length Required", new { error = "Content-Length is required for file uploads." }, false, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (contentLength < 0)
        {
            await WriteJsonAsync(stream, 400, "Bad Request", new { error = "Invalid Content-Length." }, false, cancellationToken).ConfigureAwait(false);
            return;
        }

        string? expectedSha256 = null;
        if (request.Headers.TryGetValue(
                "X-Content-SHA256",
                out var requestedHash))
        {
            expectedSha256 =
                requestedHash.Trim().ToLowerInvariant();

            if (expectedSha256.Length != 64 ||
                !expectedSha256.All(Uri.IsHexDigit))
            {
                await WriteJsonAsync(
                    stream,
                    400,
                    "Bad Request",
                    new
                    {
                        error =
                            "X-Content-SHA256 must be 64 hexadecimal characters."
                    },
                    false,
                    cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        if (request.Headers.TryGetValue("Content-Range", out var contentRange))
        {
            if (!TryParseContentRange(contentRange, out var start, out var end, out var total) || end - start + 1 != contentLength)
            {
                await WriteJsonAsync(stream, 400, "Bad Request", new { error = "Content-Range must be 'bytes start-end/total' and match Content-Length." }, false, cancellationToken).ConfigureAwait(false);
                return;
            }

            var partial = target + ".partial";
            var current = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (current != start)
            {
                await WriteJsonAsync(stream, 409, "Conflict", new { error = "Upload offset mismatch.", expectedOffset = current }, false, cancellationToken).ConfigureAwait(false);
                return;
            }

            await using (var file = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, _options.TransferBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                file.Seek(start, SeekOrigin.Begin);
                await CopyBytesAsync(stream, file, contentLength.Value, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var complete = end + 1 == total;
            string? actualSha256 = null;

            if (complete)
            {
                File.Move(
                    partial,
                    target,
                    overwrite: true);

                if (expectedSha256 is not null)
                {
                    actualSha256 =
                        await CalculateSha256Async(
                            target,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (!actualSha256.Equals(
                            expectedSha256,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(target); } catch { }

                        await WriteJsonAsync(
                            stream,
                            422,
                            "Unprocessable Content",
                            new
                            {
                                error =
                                    "Uploaded file SHA-256 does not match X-Content-SHA256.",
                                expectedSha256,
                                actualSha256
                            },
                            false,
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }
            }

            await WriteJsonAsync(
                stream,
                complete ? 201 : 202,
                complete ? "Created" : "Accepted",
                new
                {
                    path = relative,
                    complete,
                    received = end + 1,
                    total,
                    sha256 = actualSha256
                },
                keepAlive,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var temporary = target + ".uploading";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.Read, _options.TransferBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await CopyBytesAsync(stream, file, contentLength.Value, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(
                temporary,
                target,
                overwrite: true);

            string? actualSha256 = null;
            if (expectedSha256 is not null)
            {
                actualSha256 =
                    await CalculateSha256Async(
                        target,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!actualSha256.Equals(
                        expectedSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(target); } catch { }

                    await WriteJsonAsync(
                        stream,
                        422,
                        "Unprocessable Content",
                        new
                        {
                            error =
                                "Uploaded file SHA-256 does not match X-Content-SHA256.",
                            expectedSha256,
                            actualSha256
                        },
                        false,
                        cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            await WriteJsonAsync(
                stream,
                201,
                "Created",
                new
                {
                    path = relative,
                    bytes = contentLength.Value,
                    complete = true,
                    sha256 = actualSha256
                },
                keepAlive,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try { File.Delete(temporary); } catch { }
            throw;
        }
    }

    private static async Task<string>
        CalculateSha256Async(
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

    private static bool TryParseRange(string value, long fileLength, out long start, out long end)
    {
        start = 0;
        end = 0;
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || fileLength <= 0)
            return false;

        var span = value.AsSpan(6);
        var dash = span.IndexOf('-');
        if (dash < 0)
            return false;

        var left = span[..dash];
        var right = span[(dash + 1)..];
        if (left.Length == 0)
        {
            if (!long.TryParse(right, out var suffix) || suffix <= 0)
                return false;
            suffix = Math.Min(suffix, fileLength);
            start = fileLength - suffix;
            end = fileLength - 1;
            return true;
        }

        if (!long.TryParse(left, out start) || start < 0 || start >= fileLength)
            return false;
        if (right.Length == 0)
            end = fileLength - 1;
        else if (!long.TryParse(right, out end) || end < start)
            return false;

        end = Math.Min(end, fileLength - 1);
        return true;
    }

    private static bool TryParseContentRange(string value, out long start, out long end, out long total)
    {
        start = end = total = 0;
        if (!value.StartsWith("bytes ", StringComparison.OrdinalIgnoreCase))
            return false;
        var span = value.AsSpan(6);
        var slash = span.IndexOf('/');
        if (slash < 0)
            return false;
        var range = span[..slash];
        var dash = range.IndexOf('-');
        if (dash < 0)
            return false;
        return long.TryParse(range[..dash], out start) &&
               long.TryParse(range[(dash + 1)..], out end) &&
               long.TryParse(span[(slash + 1)..], out total) &&
               start >= 0 && end >= start && total > end;
    }

    private static bool QueryContains(string query, string key, string value)
    {
        foreach (var item in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            if (pair.Length == 2 && Uri.UnescapeDataString(pair[0]) == key && Uri.UnescapeDataString(pair[1]) == value)
                return true;
        }
        return false;
    }
}
