using System.Net;
using System.Globalization;
using System.Text;

namespace XemuTestRunner.Networking;

public sealed record HttpRequest(string Method, string Target, string Path, string Query, Version Version, IReadOnlyDictionary<string, string> Headers)
{
    public bool KeepAlive => Headers.TryGetValue("Connection", out var c) ? !c.Equals("close", StringComparison.OrdinalIgnoreCase) : Version >= HttpVersion.Version11;
    public long? ContentLength => Headers.TryGetValue("Content-Length", out var v) ? long.Parse(v, CultureInfo.InvariantCulture) : null;
}
public static class HttpRequestReader
{
    public static async Task<HttpRequest?> ReadAsync(Stream stream, int maxHeaderBytes, CancellationToken ct)
    {
        var line = await ReadLineAsync(stream, maxHeaderBytes, ct);
        if (line is null) return null;
        var used = line.Length + 2;
        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) throw new InvalidDataException("Invalid HTTP request line.");
        var version = parts[2] switch { "HTTP/1.1" => HttpVersion.Version11, "HTTP/1.0" => HttpVersion.Version10, _ => throw new InvalidDataException("Unsupported HTTP version.") };
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            line = await ReadLineAsync(stream, maxHeaderBytes - used, ct) ?? throw new EndOfStreamException(); used += line.Length + 2;
            if (used > maxHeaderBytes) throw new InvalidDataException("Headers exceed limit.");
            if (line.Length == 0) break;
            var colon = line.IndexOf(':'); if (colon < 1) throw new InvalidDataException("Invalid header.");
            if (!headers.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim())) throw new InvalidDataException("Duplicate header.");
        }
        if (headers.TryGetValue("Content-Length", out var length) && (!long.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 0))
            throw new InvalidDataException("Invalid Content-Length.");
        var target = parts[1]; var q = target.IndexOf('?');
        return new(parts[0].ToUpperInvariant(), target, q < 0 ? target : target[..q], q < 0 ? "" : target[(q + 1)..], version, headers);
    }
    private static async Task<string?> ReadLineAsync(Stream stream, int limit, CancellationToken ct)
    {
        var bytes = new List<byte>(128); var one = new byte[1];
        while (bytes.Count < limit)
        {
            var n = await stream.ReadAsync(one, ct);
            if (n == 0) return bytes.Count == 0 ? null : throw new EndOfStreamException();
            if (one[0] == '\n')
            {
                if (bytes.Count == 0 || bytes[^1] != '\r') throw new InvalidDataException("HTTP requires CRLF.");
                bytes.RemoveAt(bytes.Count - 1); return Encoding.ASCII.GetString(bytes.ToArray());
            }
            bytes.Add(one[0]);
        }
        throw new InvalidDataException("HTTP headers exceed limit.");
    }
}
