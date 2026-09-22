using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Networking;

internal sealed record AgentLogSlice(string RunId, string File, long FileBytes, long Offset,
    int Bytes, string Text, string NextCursor, bool Reset, bool More);

internal static class AgentLogReader
{
    private sealed record Cursor(string RunId, string File, long Offset, int AnchorBytes, string AnchorSha256);

    public static async Task<AgentLogSlice> ReadAsync(string results, string runId, string name,
        int maximumBytes, string? cursorText, CancellationToken ct)
    {
        if (name is not ("stdout.log" or "stderr.log" or "operator-events.jsonl" or "segments.jsonl"))
            throw new InvalidDataException("Use stdout.log, stderr.log, operator-events.jsonl or segments.jsonl.");
        var catalog = new EvidenceCatalog(results);
        await using var file = new FileStream(catalog.Resolve(runId, name), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.RandomAccess);
        var length = file.Length;
        var offset = Math.Max(0, length - maximumBytes);
        var reset = false;
        if (cursorText is not null)
        {
            var cursor = Decode(cursorText);
            if (cursor.RunId != runId || cursor.File != name)
                throw new InvalidDataException("Log cursor belongs to another run or file.");
            offset = cursor.Offset;
            if (offset > length) reset = true;
            else
            {
                var anchor = await ReadAtAsync(file, offset - cursor.AnchorBytes, cursor.AnchorBytes, ct).ConfigureAwait(false);
                reset = anchor.Length != cursor.AnchorBytes || Hash(anchor) != cursor.AnchorSha256;
            }
            if (reset) offset = 0;
        }
        var wanted = (int)Math.Min(maximumBytes, Math.Max(0, length - offset));
        var bytes = await ReadAtAsync(file, offset, wanted, ct).ConfigureAwait(false);
        var end = offset + bytes.Length;
        var anchorBytes = (int)Math.Min(64, end);
        var nextAnchor = await ReadAtAsync(file, end - anchorBytes, anchorBytes, ct).ConfigureAwait(false);
        if (nextAnchor.Length != anchorBytes)
            throw new AgentRequestException(409, "log_changed_during_read", "The log changed while the slice was being read.", "Read this log again without a cursor and inspect reset/offset fields.");
        var next = Encode(new(runId, name, end, anchorBytes, Hash(nextAnchor)));
        return new(runId, name, length, offset, bytes.Length, Encoding.UTF8.GetString(bytes), next, reset, end < length);
    }

    private static async Task<byte[]> ReadAtAsync(FileStream file, long offset, int count, CancellationToken ct)
    {
        file.Position = offset;
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var current = await file.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (current == 0) break;
            read += current;
        }
        return read == count ? buffer : buffer[..read];
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Encode(Cursor cursor) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static Cursor Decode(string text)
    {
        if (text.Length > 2048) throw new InvalidDataException("Log cursor is too long.");
        try
        {
            var padded = text.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            var cursor = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(padded));
            if (cursor is null || cursor.Offset < 0 || cursor.AnchorBytes is < 0 or > 64 ||
                cursor.AnchorBytes > cursor.Offset || cursor.AnchorSha256 is null ||
                cursor.AnchorSha256.Length != 64 || !cursor.AnchorSha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("Invalid log cursor fields.");
            return cursor;
        }
        catch (Exception error) when (error is FormatException or JsonException)
        { throw new InvalidDataException("Invalid log cursor encoding.", error); }
    }
}
