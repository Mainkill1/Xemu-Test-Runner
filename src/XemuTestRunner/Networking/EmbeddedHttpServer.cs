using System.Collections.Concurrent;
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
    private readonly UiOptions _uiOptions;
    private readonly RunnerPaths _paths;
    private readonly RunnerState _state;
    private readonly JobQueue _queue;
    private readonly XemuControlManager _control;
    private readonly Action _requestStop;
    private TcpListener? _listener;
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();

    public EmbeddedHttpServer(HttpOptions options, UiOptions uiOptions, RunnerPaths paths,
        RunnerState state, JobQueue queue, XemuControlManager control, Action requestStop)
    {
        _options = options; _uiOptions = uiOptions; _paths = paths; _state = state;
        _queue = queue; _control = control; _requestStop = requestStop;
    }
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return;
        _listener = new TcpListener(await ResolveBindAddressAsync(_options.BindAddress), _options.Port);
        _listener.Start();
        var advertised = NetworkEndpointResolver.Resolve(_options);
        _state.SetHttpEndpoint(advertised.Url);
        using var registration = cancellationToken.Register(() =>
        {
            _listener?.Stop();
            foreach (var client in _clients.Keys) client.Dispose();
        });
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                client.NoDelay = true;
                _clients.TryAdd(client, 0);
                _ = HandleClientAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (SocketException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            _listener.Stop(); _state.SetHttpEndpoint(null);
            foreach (var client in _clients.Keys) client.Dispose();
        }
    }
    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                await using var network = client.GetStream();
                await using var stream = new BufferedStream(network, 65536);
                while (!ct.IsCancellationRequested)
                {
                    HttpRequest? request;
                    try { request = await HttpRequestReader.ReadAsync(stream, _options.MaxHeaderBytes, ct); }
                    catch (InvalidDataException e) { await WriteJsonAsync(stream, 400, "Bad Request", new { error = e.Message }, false, ct); return; }
                    if (request is null) return;
                    if (request.Headers.ContainsKey("Transfer-Encoding"))
                    { await WriteJsonAsync(stream, 501, "Not Implemented", new { error = "Send Content-Length; chunked request encoding is not supported." }, false, ct); return; }
                    if ((request.Method is "GET" or "HEAD") && request.ContentLength.GetValueOrDefault() != 0)
                    { await WriteJsonAsync(stream, 400, "Bad Request", new { error = "GET and HEAD must not include a body." }, false, ct); return; }
                    if (request.Headers.TryGetValue("Expect", out var expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
                    { await WriteAsciiAsync(stream, "HTTP/1.1 100 Continue\r\n\r\n", ct); await stream.FlushAsync(ct); }
                    if (!await RouteAsync(stream, request, request.KeepAlive, ct) || !request.KeepAlive) return;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { }
            catch (Exception e) { System.Diagnostics.Trace.TraceError("HTTP request failed: " + e); }
            finally { _clients.TryRemove(client, out _); }
        }
    }
    private async Task<bool> RouteAsync(Stream stream, HttpRequest request, bool keepAlive, CancellationToken ct)
    {
        var evidence = await TryEvidenceRouteAsync(stream, request, keepAlive, ct);
        if (evidence.HasValue) return evidence.Value;
        if (request.Method == "GET")
        {
            switch (request.Path)
            {
                case "/": await WriteHtmlAsync(stream, WebPages.Home(_uiOptions.WebRefreshMs), keepAlive, ct); return true;
                case "/control": await WriteHtmlAsync(stream, WebPages.Control(_uiOptions.WebRefreshMs, _uiOptions.LivePreviewIntervalMs, _uiOptions.LivePreviewEnabled), keepAlive, ct); return true;
                case "/api/v1/health": await WriteJsonAsync(stream, 200, "OK", new { status = "ok", timestampUtc = DateTimeOffset.UtcNow }, keepAlive, ct); return true;
                case "/api/v1/status": await WriteJsonAsync(stream, 200, "OK", _state.Snapshot(), keepAlive, ct); return true;
                case "/api/v1/control": await WriteJsonAsync(stream, 200, "OK", _control.Snapshot(), keepAlive, ct); return true;
                case "/api/v1/queue": await WriteJsonAsync(stream, 200, "OK", _state.Snapshot().Queue, keepAlive, ct); return true;
                case "/api/v1/metrics/latest":
                    var metric = _state.Snapshot().LatestMetric;
                    if (metric is null) await WriteEmptyAsync(stream, 204, "No Content", keepAlive, ct);
                    else await WriteJsonAsync(stream, 200, "OK", metric, keepAlive, ct);
                    return true;
                case "/api/v1/preview":
                    if (!await EnsureOperationAllowedAsync(stream, "preview", keepAlive, ct))
                        return true;
                    await HandleSharedPreviewAsync(stream, keepAlive, ct);
                    return true;
                case "/api/v1/screenshot":
                    if (!await EnsureOperationAllowedAsync(stream, "preview", keepAlive, ct))
                        return true;
                    return await HandleScreenshotAsync(stream, request, keepAlive, ct);
                case "/api/v1/input/record": await WriteJsonAsync(stream, 200, "OK", _control.RecordingSnapshot(), keepAlive, ct); return true;
            }
        }
        if (request.Method == "POST")
        {
            switch (request.Path)
            {
                case "/api/v1/input/press":
                    if (!await EnsureOperationAllowedAsync(stream, "input", keepAlive, ct))
                        return true;
                    return await HandleButtonPressAsync(stream, request, keepAlive, ct);
                case "/api/v1/xemu/pause":
                    if (!await EnsureOperationAllowedAsync(stream, "pause", keepAlive, ct))
                        return true;
                    Activity.Mark("pause", null);
                    await HandlePauseAsync(stream, request, keepAlive, ct);
                    return true;
                case "/api/v1/xemu/resume":
                    if (!await EnsureOperationAllowedAsync(stream, "pause", keepAlive, ct))
                        return true;
                    Activity.Mark("resume", null);
                    await HandleResumeAsync(stream, request, keepAlive, ct);
                    return true;
                case "/api/v1/xemu/quit":
                    if (!await EnsureOperationAllowedAsync(stream, "pause", keepAlive, ct))
                        return true;
                    if (!_control.HasActiveSession)
                    {
                        await WriteJsonAsync(
                            stream,
                            409,
                            "Conflict",
                            new { error = "No active xemu target." },
                            keepAlive,
                            ct).ConfigureAwait(false);
                        return true;
                    }
                    Activity.Mark("manual_input", new { action = "quit" });
                    await _control.QuitAsync(ct).ConfigureAwait(false);
                    await WriteJsonAsync(
                        stream,
                        202,
                        "Accepted",
                        new { quitting = true },
                        keepAlive,
                        ct).ConfigureAwait(false);
                    return true;
                case "/api/v1/input/record/start": await HandleRecordingActionAsync(stream, request, keepAlive, "start", ct); return true;
                case "/api/v1/input/record/stop": await HandleRecordingActionAsync(stream, request, keepAlive, "stop", ct); return true;
                case "/api/v1/input/record/clear": await HandleRecordingActionAsync(stream, request, keepAlive, "clear", ct); return true;
                case "/api/v1/runner/stop":
                    await WriteJsonAsync(stream, 202, "Accepted", new { stopping = true }, false, ct); _requestStop(); return false;
                case "/api/v1/jobs":
                    await WriteJsonAsync(stream, 409, "Conflict", new { error = "Stage a complete directory with job.json and the candidate executable in Pending." }, false, ct); return false;
            }
        }
        const string prefix = "/api/v1/files/";
        if (request.Path.StartsWith(prefix, StringComparison.Ordinal))
        {
            if (!await EnsureOperationAllowedAsync(stream, "bulk_transfer", keepAlive, ct))
                return true;

            var relative = request.Path[prefix.Length..];
            using var transfer = Activity.TrackTransfer(new { method = request.Method, file = relative });
            // A dedicated transfer connection avoids interpreting unread bodies as a subsequent request on failure.
            if (request.Method is "GET" or "HEAD") { await HandleFileReadAsync(stream, request, relative, false, ct); return false; }
            if (request.Method is "POST" or "PUT") { await HandleFileUploadAsync(stream, request, relative, false, ct); return false; }
        }
        await WriteJsonAsync(stream, 404, "Not Found", new { error = "Route not found." }, false, ct); return false;
    }
    private async Task<bool> EnsureOperationAllowedAsync(
        Stream stream,
        string operation,
        bool keepAlive,
        CancellationToken cancellationToken)
    {
        var snapshot = _state.Snapshot();
        if (snapshot.CurrentJob is null)
            return true;

        var policy = snapshot.Operations;
        var allowed = operation switch
        {
            "preview" => policy.PreviewAllowed,
            "input" => policy.ManualInputAllowed,
            "pause" => policy.PauseResumeAllowed,
            "diagnostic" => policy.DiagnosticsAllowed,
            "bulk_transfer" => policy.BulkTransfersAllowed,
            _ => true
        };

        if (allowed)
            return true;

        await WriteJsonAsync(
            stream,
            409,
            "Conflict",
            new
            {
                error =
                    $"Operation '{operation}' is blocked by the active job's '{policy.Mode}' operation policy.",
                mode = policy.Mode,
                operation
            },
            keepAlive,
            cancellationToken).ConfigureAwait(false);

        return false;
    }

    private async Task<bool> HandleScreenshotAsync(Stream stream, HttpRequest request, bool keepAlive, CancellationToken ct)
    {
        if (!_control.HasActiveSession) { await WriteJsonAsync(stream, 409, "Conflict", new { error = "No active xemu." }, keepAlive, ct); return true; }
        string path;
        try { Activity.Mark("screenshot", null); path = await _control.CaptureScreenshotAsync(GetQueryValue(request.Query, "name"), ct); }
        catch (Exception e) when (e is IOException or InvalidDataException or TimeoutException or InvalidOperationException or SocketException)
        { await WriteJsonAsync(stream, 503, "Service Unavailable", new { error = e.Message }, false, ct); return false; }
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, _options.TransferBufferBytes, FileOptions.Asynchronous);
        await WriteHeadersAsync(stream, 200, "OK", new Dictionary<string, string>
        {
            ["Content-Type"] = "image/png", ["Content-Length"] = file.Length.ToString(), ["Cache-Control"] = "no-store",
            ["Content-Disposition"] = $"inline; filename=\"{EscapeHeaderValue(Path.GetFileName(path))}\""
        }, keepAlive, ct);
        await CopyBytesAsync(file, stream, file.Length, ct); await stream.FlushAsync(ct); return true;
    }
    private async Task<bool> HandleButtonPressAsync(Stream stream, HttpRequest request, bool keepAlive, CancellationToken ct)
    {
        if (request.ContentLength is not long length || length is < 1 or > 16384)
        { await WriteJsonAsync(stream, 400, "Bad Request", new { error = "Input body requires Content-Length between 1 and 16384." }, false, ct); return false; }
        var bytes = new byte[(int)length]; await ReadExactlyAsync(stream, bytes, ct);
        if (!_control.HasActiveSession || _control.Snapshot().Paused)
        { await WriteJsonAsync(stream, 409, "Conflict", new { error = "Input requires an active, unpaused xemu." }, keepAlive, ct); return true; }
        try
        {
            var input = JsonSerializer.Deserialize<ButtonPressRequest>(bytes, ConfigLoader.JsonOptions) ?? throw new InvalidDataException("Empty input.");
            if (string.IsNullOrWhiteSpace(input.Button) || input.DurationMs is < 1 or > 60000) throw new InvalidDataException("Invalid button or duration.");
            Activity.Mark("manual_input", new { input.Button, input.DurationMs });
            await _control.PressButtonAsync(input.Button, input.DurationMs, ct);
            await WriteJsonAsync(stream, 200, "OK", new { accepted = true, button = input.Button, durationMs = input.DurationMs }, keepAlive, ct);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException)
        { await WriteJsonAsync(stream, 400, "Bad Request", new { error = e.Message }, keepAlive, ct); }
        catch (InvalidOperationException e) { await WriteJsonAsync(stream, 503, "Service Unavailable", new { error = e.Message }, keepAlive, ct); }
        return true;
    }
    private sealed class ButtonPressRequest { public string Button { get; set; } = ""; public int? DurationMs { get; set; } }
}
