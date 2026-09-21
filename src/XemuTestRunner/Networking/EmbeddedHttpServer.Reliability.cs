using System.Globalization;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Networking;

public sealed partial class EmbeddedHttpServer
{
    public ReliabilityOptions Reliability { get; init; } = new();
    public ActivityHub Activity { get; init; } = new();
    private readonly PreviewCache _previewCache = new();

    private async Task HandleSharedPreviewAsync(Stream stream, bool keepAlive, CancellationToken ct)
    {
        var runId = _state.Snapshot().RunId;
        if (!_uiOptions.LivePreviewEnabled || runId is null || !_control.HasActiveSession)
        {
            await WriteJsonAsync(stream, 409, "Conflict", new { error = "No active preview is available." }, keepAlive, ct); return;
        }
        PreviewFrame frame;
        try
        {
            frame = await _previewCache.GetAsync(runId, _uiOptions.LivePreviewIntervalMs, async token =>
            {
                if (_state.Snapshot().RunId != runId) throw new InvalidOperationException("Active run changed.");
                Activity.Mark("preview_capture", null);
                var path = await _control.CapturePreviewAsync(token);
                try
                {
                    if (_state.Snapshot().RunId != runId) throw new InvalidOperationException("Active run changed during capture.");
                    if (new FileInfo(path).Length > Reliability.MaxPreviewBytes) throw new IOException("Preview exceeds MaxPreviewBytes.");
                    return await File.ReadAllBytesAsync(path, token);
                }
                finally { if (File.Exists(path)) File.Delete(path); }
            }, ct);
            if (_state.Snapshot().RunId != runId) throw new InvalidOperationException("Active run changed.");
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException)
        { await WriteJsonAsync(stream, 503, "Service Unavailable", new { error = e.Message }, keepAlive, ct); return; }
        await WriteHeadersAsync(stream, 200, "OK", new Dictionary<string, string>
        {
            ["Content-Type"] = "image/png", ["Content-Length"] = frame.Bytes.LongLength.ToString(CultureInfo.InvariantCulture),
            ["Cache-Control"] = "no-store", ["X-Run-Id"] = frame.RunId,
            ["X-Captured-Utc"] = frame.CapturedUtc.ToString("O", CultureInfo.InvariantCulture)
        }, keepAlive, ct);
        await stream.WriteAsync(frame.Bytes, ct);
        await stream.FlushAsync(ct);
    }

    private async Task<bool?> TryEvidenceRouteAsync(Stream stream, HttpRequest request, bool keepAlive, CancellationToken ct)
    {
        if (request.Method == "GET" && request.Path == "/results")
        { await WriteHtmlAsync(stream, EvidencePage.Html, keepAlive, ct); return true; }
        if (request.Method == "GET" && request.Path == "/api/v1/quality")
        { await WriteJsonAsync(stream, 200, "OK", Activity.Snapshot(), keepAlive, ct); return true; }
        var catalog = new EvidenceCatalog(_paths.Results);
        if (request.Method == "GET" && request.Path == "/api/v1/runs")
        { await WriteJsonAsync(stream, 200, "OK", catalog.ListRuns(Reliability.EvidenceListLimit), keepAlive, ct); return true; }
        const string prefix = "/api/v1/runs/";
        if (!request.Path.StartsWith(prefix, StringComparison.Ordinal) || request.Method is not ("GET" or "HEAD")) return null;
        var parts = request.Path[prefix.Length..].Split('/', 3);
        var runId = Uri.UnescapeDataString(parts[0]);
        try
        {
            if (request.Method == "HEAD" && !(parts.Length == 3 && parts[1] == "artifacts"))
            { await WriteEmptyAsync(stream, 405, "Method Not Allowed", keepAlive, ct); return true; }
            if (parts.Length == 1)
            {
                await WriteJsonAsync(stream, 200, "OK", new { RunId = runId, Artifacts = catalog.Artifacts(runId) }, keepAlive, ct);
                return true;
            }
            if (parts[1] == "tail" && request.Method == "GET")
            {
                var name = GetQueryValue(request.Query, "file") ?? "stdout.log";
                var bytes = int.TryParse(GetQueryValue(request.Query, "bytes"), out var parsed) ? parsed : 32768;
                await WriteJsonAsync(stream, 200, "OK", await catalog.TailAsync(runId, name, bytes, ct), keepAlive, ct);
                return true;
            }
            if (parts[1] == "artifacts" && parts.Length == 3)
            {
                var path = catalog.Resolve(runId, Uri.UnescapeDataString(parts[2]));
                // Open before sending any headers. Downloads use the actual handle length.
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    _options.TransferBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
                FileRange range;
                try { range = FileRange.Parse(request.Headers.GetValueOrDefault("Range"), file.Length); }
                catch (InvalidDataException)
                {
                    await WriteHeadersAsync(stream, 416, "Range Not Satisfiable", new Dictionary<string, string>
                    { ["Content-Range"] = $"bytes */{file.Length}", ["Content-Length"] = "0" }, keepAlive, ct);
                    await stream.FlushAsync(ct); return true;
                }
                using var transfer = range.Length > 1024 * 1024 && request.Method == "GET" ? Activity.TrackTransfer(new { runId, file = Path.GetFileName(path), bytes = range.Length }) : null;
                var headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "application/octet-stream",
                    ["Content-Length"] = range.Length.ToString(CultureInfo.InvariantCulture), ["Accept-Ranges"] = "bytes",
                    ["Cache-Control"] = "no-store", ["Content-Disposition"] = $"attachment; filename=\"{EscapeHeaderValue(Path.GetFileName(path))}\""
                };
                if (range.Partial) headers["Content-Range"] = $"bytes {range.Start}-{range.Start + range.Length - 1}/{file.Length}";
                await WriteHeadersAsync(stream, range.Partial ? 206 : 200, range.Partial ? "Partial Content" : "OK", headers, keepAlive, ct);
                if (request.Method != "HEAD") { file.Seek(range.Start, SeekOrigin.Begin); await CopyBytesAsync(file, stream, range.Length, ct); }
                await stream.FlushAsync(ct); return true;
            }
            await WriteJsonAsync(stream, 404, "Not Found", new { error = "Evidence route not found." }, keepAlive, ct); return true;
        }
        catch (FileNotFoundException e) { await WriteJsonAsync(stream, 404, "Not Found", new { error = e.Message }, false, ct); return false; }
        catch (DirectoryNotFoundException e) { await WriteJsonAsync(stream, 404, "Not Found", new { error = e.Message }, false, ct); return false; }
        catch (InvalidDataException e) { await WriteJsonAsync(stream, 400, "Bad Request", new { error = e.Message }, false, ct); return false; }
    }
}
