using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace XemuTestRunner.Control;

public sealed class XemuQmpClient
{
    // One foreground runner owns one xemu. Serialize short-lived QMP connections
    // so watchdog, operator commands and PNG requests cannot overlap handshakes.
    private static readonly SemaphoreSlim Wire = new(1, 1);
    private readonly string _host;
    private readonly int _port;
    public XemuQmpClient(string host, int port) { _host = host; _port = port; }
    public async Task<JsonElement> ExecuteAsync(string command, object? arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await Wire.WaitAsync(cancellationToken);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(_host, _port, deadline.Token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                var greeting = await reader.ReadLineAsync(deadline.Token) ?? throw new IOException("QMP greeting missing.");
                using (var doc = JsonDocument.Parse(greeting))
                    if (!doc.RootElement.TryGetProperty("QMP", out _)) throw new InvalidDataException("Not a QMP endpoint.");
                await SendAsync(writer, 1, "qmp_capabilities", null, deadline.Token);
                _ = await ReadAsync(reader, 1, deadline.Token);
                await SendAsync(writer, 2, command, arguments, deadline.Token);
                return await ReadAsync(reader, 2, deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new TimeoutException($"QMP {command} exceeded {timeout.TotalMilliseconds:0} ms."); }
        }
        finally { Wire.Release(); }
    }
    private static async Task SendAsync(StreamWriter writer, int id, string command, object? arguments, CancellationToken ct)
    {
        var payload = arguments is null ? JsonSerializer.Serialize(new { execute = command, id })
            : JsonSerializer.Serialize(new { execute = command, arguments, id });
        await writer.WriteLineAsync(payload.AsMemory(), ct);
    }
    private static async Task<JsonElement> ReadAsync(StreamReader reader, int expected, CancellationToken ct)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(ct) ?? throw new IOException("QMP disconnected.");
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line); var root = doc.RootElement;
            if (root.TryGetProperty("event", out _)) continue;
            if (!root.TryGetProperty("id", out var id) || !id.TryGetInt32(out var value) || value != expected) continue;
            if (root.TryGetProperty("error", out var error))
            {
                var errorClass = error.TryGetProperty("class", out var classValue)
                    ? classValue.GetString() ?? "Unknown"
                    : "Unknown";
                var description = error.TryGetProperty("desc", out var descValue)
                    ? descValue.GetString() ?? error.ToString()
                    : error.ToString();
                throw new QmpCommandException(errorClass, description);
            }
            if (!root.TryGetProperty("return", out var result)) throw new InvalidDataException("Malformed QMP result.");
            return result.Clone();
        }
    }
}

public sealed class QmpCommandException(string errorClass, string description)
    : InvalidOperationException($"QMP command failed ({errorClass}): {description}")
{
    public string ErrorClass { get; } = errorClass;
    public string Description { get; } = description;
}
