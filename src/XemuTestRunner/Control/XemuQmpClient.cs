using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace XemuTestRunner.Control;

public sealed class XemuQmpClient
{
    private readonly string _host;
    private readonly int _port;

    public XemuQmpClient(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public async Task<JsonElement> ExecuteAsync(
        string command,
        object? arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        using var client = new TcpClient();
        await client.ConnectAsync(_host, _port, timeoutCts.Token).ConfigureAwait(false);

        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        var greeting = await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(greeting))
            throw new IOException("QMP connection closed before greeting.");

        using (var greetingDocument = JsonDocument.Parse(greeting))
        {
            if (!greetingDocument.RootElement.TryGetProperty("QMP", out _))
                throw new InvalidDataException("Endpoint did not return a QMP greeting.");
        }

        await SendAsync(writer, 1, "qmp_capabilities", null, timeoutCts.Token).ConfigureAwait(false);
        _ = await ReadResponseAsync(reader, 1, timeoutCts.Token).ConfigureAwait(false);

        await SendAsync(writer, 2, command, arguments, timeoutCts.Token).ConfigureAwait(false);
        return await ReadResponseAsync(reader, 2, timeoutCts.Token).ConfigureAwait(false);
    }

    private static async Task SendAsync(
        StreamWriter writer,
        int id,
        string command,
        object? arguments,
        CancellationToken cancellationToken)
    {
        var payload = arguments is null
            ? JsonSerializer.Serialize(new { execute = command, id })
            : JsonSerializer.Serialize(new { execute = command, arguments, id });

        await writer.WriteLineAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonElement> ReadResponseAsync(
        StreamReader reader,
        int expectedId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                throw new IOException("QMP connection closed while awaiting a response.");
            if (string.IsNullOrWhiteSpace(line))
                continue;

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (root.TryGetProperty("event", out _))
                continue;

            if (!root.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.Number ||
                id.GetInt32() != expectedId)
                continue;

            if (root.TryGetProperty("error", out var error))
                throw new InvalidOperationException($"QMP command failed: {error}");

            if (!root.TryGetProperty("return", out var result))
                throw new InvalidDataException("QMP response did not contain return or error.");

            return result.Clone();
        }
    }
}
