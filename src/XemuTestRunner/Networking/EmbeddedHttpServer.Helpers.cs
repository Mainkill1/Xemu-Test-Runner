using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using XemuTestRunner.Config;

namespace XemuTestRunner.Networking;

public sealed partial class EmbeddedHttpServer
{
    private async Task CopyBytesAsync(Stream source, Stream destination, long bytes, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_options.TransferBufferBytes);
        try
        {
            var remaining = bytes;
            while (remaining > 0)
            {
                var wanted = (int)Math.Min(buffer.Length, remaining);
                var read = await source.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException($"Transfer ended with {remaining} bytes remaining.");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task ReadExactlyAsync(Stream source, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await source.ReadAsync(destination[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("Request body ended early.");
            offset += read;
        }
    }

    private static async Task DrainBodyAsync(Stream source, long bytes, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            var remaining = bytes;
            while (remaining > 0)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static Task WriteApiErrorAsync(
        Stream stream,
        int status,
        string reason,
        string code,
        string message,
        string hint,
        bool keepAlive,
        CancellationToken cancellationToken,
        object? details = null) =>
        WriteJsonAsync(
            stream,
            status,
            reason,
            new ApiErrorResponse(
                message,
                code,
                hint,
                status,
                "/api/v1/help",
                details),
            keepAlive,
            cancellationToken);

    private static async Task WriteJsonAsync(Stream stream, int code, string reason, object value, bool keepAlive, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value, ConfigLoader.JsonOptions);
        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/json; charset=utf-8",
            ["Content-Length"] = body.Length.ToString()
        };
        await WriteHeadersAsync(stream, code, reason, headers, keepAlive, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteHtmlAsync(
        Stream stream,
        string html,
        bool keepAlive,
        CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = "text/html; charset=utf-8",
            ["Content-Length"] = body.Length.ToString(),
            ["Cache-Control"] = "no-store"
        };

        await WriteHeadersAsync(
            stream,
            200,
            "OK",
            headers,
            keepAlive,
            cancellationToken).ConfigureAwait(false);

        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteEmptyAsync(Stream stream, int code, string reason, bool keepAlive, CancellationToken cancellationToken)
    {
        await WriteHeadersAsync(stream, code, reason, new Dictionary<string, string> { ["Content-Length"] = "0" }, keepAlive, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteHeadersAsync(Stream stream, int code, string reason, IReadOnlyDictionary<string, string> headers, bool keepAlive, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 ").Append(code).Append(' ').Append(reason).Append("\r\n");
        builder.Append("Server: XemuTestRunner\r\n");
        builder.Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n");
        foreach (var header in headers)
            builder.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
        builder.Append("\r\n");
        await WriteAsciiAsync(stream, builder.ToString(), cancellationToken).ConfigureAwait(false);
    }

    private static Task WriteAsciiAsync(Stream stream, string value, CancellationToken cancellationToken) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(value), cancellationToken).AsTask();

    private static string EscapeHeaderValue(string value) => value.Replace("\"", "'", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);

    private static string? GetQueryValue(string query, string key)
    {
        foreach (var item in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            if (!string.Equals(Uri.UnescapeDataString(pair[0]), key, StringComparison.OrdinalIgnoreCase))
                continue;

            return pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "";
        }

        return null;
    }

    private static async Task<IPAddress> ResolveBindAddressAsync(string value)
    {
        if (value == "*" || value == "0.0.0.0")
            return IPAddress.Any;
        if (value == "::")
            return IPAddress.IPv6Any;
        if (IPAddress.TryParse(value, out var parsed))
            return parsed;

        var addresses = await Dns.GetHostAddressesAsync(value).ConfigureAwait(false);
        return addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.First();
    }
}
