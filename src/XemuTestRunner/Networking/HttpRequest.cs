using System.Text;

namespace XemuTestRunner.Networking;

public sealed record HttpRequest(
    string Method,
    string Target,
    string Path,
    string Query,
    Version Version,
    IReadOnlyDictionary<string, string> Headers)
{
    public bool KeepAlive
    {
        get
        {
            if (Headers.TryGetValue("Connection", out var connection))
                return !connection.Equals("close", StringComparison.OrdinalIgnoreCase);
            return Version.Major > 1 || (Version.Major == 1 && Version.Minor >= 1);
        }
    }

    public long? ContentLength => Headers.TryGetValue("Content-Length", out var value) && long.TryParse(value, out var length)
        ? length
        : null;
}

public static class HttpRequestReader
{
    public static async Task<HttpRequest?> ReadAsync(Stream stream, int maxHeaderBytes, CancellationToken cancellationToken)
    {
        var consumed = 0;
        var requestLine = await ReadLineAsync(stream, maxHeaderBytes, cancellationToken).ConfigureAwait(false);
        if (requestLine is null)
            return null;
        consumed += requestLine.Length + 2;
        if (requestLine.Length == 0)
            return null;

        var requestParts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestParts.Length != 3)
            throw new InvalidDataException("Invalid HTTP request line.");

        var method = requestParts[0].ToUpperInvariant();
        var target = requestParts[1];
        var version = requestParts[2] switch
        {
            "HTTP/1.0" => HttpVersion.Version10,
            "HTTP/1.1" => HttpVersion.Version11,
            _ => throw new InvalidDataException("Unsupported HTTP version.")
        };

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var line = await ReadLineAsync(stream, maxHeaderBytes - consumed, cancellationToken).ConfigureAwait(false)
                ?? throw new EndOfStreamException("Connection ended while reading headers.");
            consumed += line.Length + 2;
            if (consumed > maxHeaderBytes)
                throw new InvalidDataException("HTTP headers exceeded configured limit.");
            if (line.Length == 0)
                break;

            var colon = line.IndexOf(':');
            if (colon <= 0)
                throw new InvalidDataException("Invalid HTTP header.");
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var queryIndex = target.IndexOf('?');
        var path = queryIndex >= 0 ? target[..queryIndex] : target;
        var query = queryIndex >= 0 ? target[(queryIndex + 1)..] : "";
        return new HttpRequest(method, target, path, query, version, headers);
    }

    private static async Task<string?> ReadLineAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        if (maxBytes <= 0)
            throw new InvalidDataException("HTTP headers exceeded configured limit.");

        var bytes = new List<byte>(128);
        var one = new byte[1];
        while (bytes.Count < maxBytes)
        {
            var read = await stream.ReadAsync(one.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return bytes.Count == 0 ? null : throw new EndOfStreamException("Unexpected end of HTTP line.");

            if (one[0] == (byte)'\n')
            {
                if (bytes.Count > 0 && bytes[^1] == (byte)'\r')
                    bytes.RemoveAt(bytes.Count - 1);
                return Encoding.ASCII.GetString(bytes.ToArray());
            }
            bytes.Add(one[0]);
        }

        throw new InvalidDataException("HTTP line exceeded configured limit.");
    }
}
