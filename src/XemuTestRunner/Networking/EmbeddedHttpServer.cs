using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Control;
using XemuTestRunner.Queue;
using XemuTestRunner.Runtime;

namespace XemuTestRunner.Networking;

public sealed partial class EmbeddedHttpServer
{
    private readonly HttpOptions _options;
    private readonly RunnerPaths _paths;
    private readonly RunnerState _state;
    private readonly JobQueue _queue;
    private readonly XemuControlManager _control;
    private readonly Action _requestStop;
    private TcpListener? _listener;

    public EmbeddedHttpServer(
        HttpOptions options,
        RunnerPaths paths,
        RunnerState state,
        JobQueue queue,
        XemuControlManager control,
        Action requestStop)
    {
        _options = options;
        _paths = paths;
        _state = state;
        _queue = queue;
        _control = control;
        _requestStop = requestStop;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            return;

        var address = await ResolveBindAddressAsync(_options.BindAddress).ConfigureAwait(false);
        _listener = new TcpListener(address, _options.Port);
        _listener.Start();
        _state.SetHttpEndpoint($"http://{_options.BindAddress}:{_options.Port}");

        using var registration = cancellationToken.Register(() =>
        {
            try { _listener.Stop(); } catch { }
        });

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                client.NoDelay = true;
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                _ = HandleClientAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            try { _listener.Stop(); } catch { }
            _listener = null;
            _state.SetHttpEndpoint(null);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            await using var network = client.GetStream();
            await using var stream = new BufferedStream(network, 64 * 1024);

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    HttpRequest? request;
                    try
                    {
                        request = await HttpRequestReader.ReadAsync(
                            stream,
                            _options.MaxHeaderBytes,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (InvalidDataException ex)
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

                    if (request is null)
                        return;

                    var keepAlive = request.KeepAlive;

                    if (request.Headers.TryGetValue("Transfer-Encoding", out var transferEncoding) &&
                        !string.Equals(transferEncoding, "identity", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteJsonAsync(
                            stream,
                            501,
                            "Not Implemented",
                            new { error = "Chunked request bodies are not supported. Send Content-Length." },
                            false,
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    if (request.Headers.TryGetValue("Expect", out var expect) &&
                        expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteAsciiAsync(
                            stream,
                            "HTTP/1.1 100 Continue\r\n\r\n",
                            cancellationToken).ConfigureAwait(false);
                        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }

                    var consumedBody = await RouteAsync(
                        stream,
                        request,
                        keepAlive,
                        cancellationToken).ConfigureAwait(false);

                    if (!keepAlive || !consumedBody)
                        return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
        }
    }

    private async Task<bool> RouteAsync(
        Stream stream,
        HttpRequest request,
        bool keepAlive,
        CancellationToken cancellationToken)
    {
        if (request.Method == "GET" && request.Path == "/api/v1/health")
        {
            await WriteJsonAsync(
                stream,
                200,
                "OK",
                new { status = "ok", timestampUtc = DateTimeOffset.UtcNow },
                keepAlive,
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (request.Method == "GET" && request.Path == "/api/v1/status")
        {
            await WriteJsonAsync(
                stream,
                200,
                "OK",
                _state.Snapshot(),
                keepAlive,
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (request.Method == "GET" && request.Path == "/api/v1/metrics/latest")
        {
            var metric = _state.Snapshot().LatestMetric;
            if (metric is null)
                await WriteEmptyAsync(stream, 204, "No Content", keepAlive, cancellationToken).ConfigureAwait(false);
            else
                await WriteJsonAsync(stream, 200, "OK", metric, keepAlive, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (request.Method == "GET" && request.Path == "/api/v1/queue")
        {
            await WriteJsonAsync(
                stream,
                200,
                "OK",
                _state.Snapshot().Queue,
                keepAlive,
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (request.Method == "GET" && request.Path == "/api/v1/screenshot")
        {
            await HandleScreenshotAsync(stream, request, keepAlive, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (request.Method == "POST" && request.Path == "/api/v1/input/press")
        {
            await HandleButtonPressAsync(stream, request, keepAlive, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (request.Method == "POST" && request.Path == "/api/v1/runner/stop")
        {
            if ((request.ContentLength ?? 0) > 0)
                await DrainBodyAsync(stream, request.ContentLength!.Value, cancellationToken).ConfigureAwait(false);

            await WriteJsonAsync(
                stream,
                202,
                "Accepted",
                new { stopping = true },
                false,
                cancellationToken).ConfigureAwait(false);

            _requestStop();
            return true;
        }

        if (request.Method == "POST" && request.Path == "/api/v1/jobs")
        {
            if ((request.ContentLength ?? 0) > 0)
                await DrainBodyAsync(stream, request.ContentLength!.Value, cancellationToken).ConfigureAwait(false);

            await WriteJsonAsync(
                stream,
                409,
                "Conflict",
                new
                {
                    error = "Queue entries are self-contained directories, not standalone JSON.",
                    expected = "Place a directory containing job.json and the xemu executable under Queue/Pending."
                },
                keepAlive,
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        const string filesPrefix = "/api/v1/files/";
        if (request.Path.StartsWith(filesPrefix, StringComparison.Ordinal))
        {
            var relative = request.Path[filesPrefix.Length..];

            if (request.Method is "GET" or "HEAD")
            {
                await HandleFileReadAsync(
                    stream,
                    request,
                    relative,
                    keepAlive,
                    cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (request.Method is "PUT" or "POST")
            {
                await HandleFileUploadAsync(
                    stream,
                    request,
                    relative,
                    keepAlive,
                    cancellationToken).ConfigureAwait(false);
                return true;
            }
        }

        await WriteJsonAsync(
            stream,
            404,
            "Not Found",
            new { error = "Route not found." },
            false,
            cancellationToken).ConfigureAwait(false);

        return request.ContentLength.GetValueOrDefault() == 0;
    }

    private async Task HandleScreenshotAsync(
        Stream stream,
        HttpRequest request,
        bool keepAlive,
        CancellationToken cancellationToken)
    {
        if (!_control.HasActiveSession)
        {
            await WriteJsonAsync(
                stream,
                409,
                "Conflict",
                new { error = "No xemu test is currently active." },
                keepAlive,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var responseStarted = false;

        try
        {
            var requestedName = GetQueryValue(request.Query, "name");
            var path = await _control.CaptureScreenshotAsync(
                requestedName,
                cancellationToken).ConfigureAwait(false);

            var info = new FileInfo(path);
            await using var file = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                _options.TransferBufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var headers = new Dictionary<string, string>
            {
                ["Content-Type"] = "image/png",
                ["Content-Length"] = info.Length.ToString(),
                ["Cache-Control"] = "no-store",
                ["Content-Disposition"] = $"inline; filename=\"{EscapeHeaderValue(info.Name)}\"",
                ["X-Xemu-Screenshot"] = EscapeHeaderValue(info.Name)
            };

            await WriteHeadersAsync(
                stream,
                200,
                "OK",
                headers,
                keepAlive,
                cancellationToken).ConfigureAwait(false);
            responseStarted = true;

            await CopyBytesAsync(file, stream, info.Length, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is IOException or
            SocketException or
            InvalidDataException or
            InvalidOperationException or
            TimeoutException)
        {
            if (responseStarted)
                throw new IOException("Screenshot response failed after HTTP headers were sent.", ex);

            await WriteJsonAsync(
                stream,
                503,
                "Service Unavailable",
                new { error = ex.Message },
                false,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleButtonPressAsync(
        Stream stream,
        HttpRequest request,
        bool keepAlive,
        CancellationToken cancellationToken)
    {
        if (!_control.HasActiveSession)
        {
            if ((request.ContentLength ?? 0) > 0)
                await DrainBodyAsync(stream, request.ContentLength!.Value, cancellationToken).ConfigureAwait(false);

            await WriteJsonAsync(
                stream,
                409,
                "Conflict",
                new { error = "No xemu test is currently active." },
                keepAlive,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var length = request.ContentLength;
        if (length is null)
        {
            await WriteJsonAsync(
                stream,
                411,
                "Length Required",
                new { error = "Content-Length is required." },
                false,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (length <= 0 || length > 16 * 1024)
        {
            await WriteJsonAsync(
                stream,
                413,
                "Content Too Large",
                new { error = "Input request JSON must be between 1 byte and 16 KiB." },
                false,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var buffer = new byte[(int)length.Value];
        await ReadExactlyAsync(stream, buffer, cancellationToken).ConfigureAwait(false);

        try
        {
            var input = JsonSerializer.Deserialize<ButtonPressRequest>(
                buffer,
                ConfigLoader.JsonOptions)
                ?? throw new InvalidDataException("Input request was empty or invalid.");

            if (string.IsNullOrWhiteSpace(input.Button))
                throw new InvalidDataException("Button is required.");

            await _control.PressButtonAsync(
                input.Button,
                input.DurationMs,
                cancellationToken).ConfigureAwait(false);

            await WriteJsonAsync(
                stream,
                200,
                "OK",
                new
                {
                    button = input.Button,
                    durationMs = input.DurationMs,
                    accepted = true
                },
                keepAlive,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            await WriteJsonAsync(
                stream,
                503,
                "Service Unavailable",
                new { error = ex.Message },
                keepAlive,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            await WriteJsonAsync(
                stream,
                400,
                "Bad Request",
                new { error = ex.Message },
                keepAlive,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ButtonPressRequest
    {
        public string Button { get; set; } = "";
        public int? DurationMs { get; set; }
    }
}
