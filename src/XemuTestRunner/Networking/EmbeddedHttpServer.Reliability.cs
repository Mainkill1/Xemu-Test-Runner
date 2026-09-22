using System.Globalization;
using XemuTestRunner.Reliability;
using XemuTestRunner.Diagnostics;
using XemuTestRunner.Config;
using System.Text.Json;

namespace XemuTestRunner.Networking;

public sealed partial class EmbeddedHttpServer
{
    public ReliabilityOptions Reliability { get; init; } = new();
    public ActivityHub Activity { get; init; } = new();
    public DiagnosticHub? Diagnostics { get; init; }
    private readonly PreviewCache _previewCache = new();

    private async Task HandleSharedPreviewAsync(Stream stream, bool keepAlive, CancellationToken ct)
    {
        var runId = _state.Snapshot().RunId;
        if (!_uiOptions.LivePreviewEnabled || runId is null || !_control.HasActiveSession)
        {
            await WriteApiErrorAsync(
                stream,
                409,
                "Conflict",
                "preview_not_available",
                "No active preview is available.",
                "Start an xemu test and wait for the active run/control session before requesting preview frames.",
                keepAlive,
                ct).ConfigureAwait(false);
            return;
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
        catch (Exception e) when (
            e is IOException or
            InvalidOperationException or
            TimeoutException or
            System.Net.Sockets.SocketException)
        {
            await WriteApiErrorAsync(
                stream,
                503,
                "Service Unavailable",
                "preview_unavailable",
                e.Message,
                "Verify the active xemu target and screenshot provider, then retry. Use /api/v1/control to inspect control readiness.",
                keepAlive,
                ct).ConfigureAwait(false);
            return;
        }
        await WriteHeadersAsync(stream, 200, "OK", new Dictionary<string, string>
        {
            ["Content-Type"] = "image/png", ["Content-Length"] = frame.Bytes.LongLength.ToString(CultureInfo.InvariantCulture),
            ["Cache-Control"] = "no-store", ["X-Run-Id"] = frame.RunId,
            ["X-Captured-Utc"] = frame.CapturedUtc.ToString("O", CultureInfo.InvariantCulture)
        }, keepAlive, ct);
        await stream.WriteAsync(frame.Bytes, ct);
        await stream.FlushAsync(ct);
    }


    private async Task<bool?> TryDiagnosticRouteAsync(
        Stream stream,
        HttpRequest request,
        bool keepAlive,
        CancellationToken ct)
    {
        if (request.Path == "/diagnostics" && request.Method == "GET")
        {
            await WriteHtmlAsync(stream, DiagnosticsPage.Html, keepAlive, ct).ConfigureAwait(false);
            return true;
        }

        if (!request.Path.StartsWith("/api/v1/diagnostics", StringComparison.Ordinal))
            return null;

        if (Diagnostics is null)
        {
            await WriteApiErrorAsync(
                stream,
                503,
                "Service Unavailable",
                "diagnostics_unavailable",
                "Diagnostic hub is unavailable.",
                "Enable diagnostics in runner configuration or use a runner instance with diagnostics initialized.",
                keepAlive,
                ct).ConfigureAwait(false);
            return true;
        }

        if (request.Method == "GET" && request.Path == "/api/v1/diagnostics")
        {
            await WriteJsonAsync(stream, 200, "OK", Diagnostics.Snapshot(), keepAlive, ct).ConfigureAwait(false);
            return true;
        }

        if (request.Method == "GET" && request.Path == "/api/v1/diagnostics/tools")
        {
            await WriteJsonAsync(stream, 200, "OK", await Diagnostics.Tools.ProbeAsync(ct).ConfigureAwait(false), keepAlive, ct).ConfigureAwait(false);
            return true;
        }

        if (request.Method == "GET" && request.Path == "/api/v1/diagnostics/recipes")
        {
            await WriteJsonAsync(stream, 200, "OK", Diagnostics.Recipes(), keepAlive, ct).ConfigureAwait(false);
            return true;
        }

        if (request.Method == "POST" && request.Path == "/api/v1/diagnostics/run")
        {
            if (!await EnsureOperationAllowedAsync(stream, "diagnostic", keepAlive, ct).ConfigureAwait(false))
                return true;
            if (request.ContentLength is not long length || length is < 1 or > 65536)
            {
                await WriteApiErrorAsync(
                    stream,
                    400,
                    "Bad Request",
                    "request_body_invalid",
                    "Diagnostic request requires a JSON body with Content-Length between 1 and 65536 bytes.",
                    "POST either an Id field for a configured recipe or a Recipe object for an ad-hoc diagnostic. See /api/v1/help for examples.",
                    false,
                    ct).ConfigureAwait(false);
                return false;
            }

            var body = new byte[(int)length];
            await ReadExactlyAsync(stream, body, ct).ConfigureAwait(false);

            try
            {
                var command = JsonSerializer.Deserialize<DiagnosticRunRequest>(
                    body,
                    ConfigLoader.JsonOptions)
                    ?? throw new InvalidDataException("Empty diagnostic request.");

                DiagnosticResult result;
                if (!string.IsNullOrWhiteSpace(command.Id))
                {
                    result = await Diagnostics.RunByIdAsync(command.Id, ct).ConfigureAwait(false);
                }
                else if (command.Recipe is not null)
                {
                    command.Recipe.Validate();
                    result = await Diagnostics.RunAdHocAsync(command.Recipe, ct).ConfigureAwait(false);
                }
                else
                {
                    throw new InvalidDataException("Specify Id or Recipe.");
                }

                await WriteJsonAsync(stream, 200, "OK", result, keepAlive, ct).ConfigureAwait(false);
            }
            catch (KeyNotFoundException ex)
            {
                await WriteApiErrorAsync(
                    stream,
                    404,
                    "Not Found",
                    "diagnostic_not_found",
                    ex.Message,
                    "GET /api/v1/diagnostics/recipes and use one of the configured recipe IDs, or submit a valid ad-hoc Recipe.",
                    keepAlive,
                    ct).ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                await WriteApiErrorAsync(
                    stream,
                    400,
                    "Bad Request",
                    "diagnostic_request_invalid",
                    ex.Message,
                    "Correct the diagnostic fields using GET /api/v1/diagnostics/recipes or the examples in /api/v1/help.",
                    keepAlive,
                    ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                await WriteApiErrorAsync(
                    stream,
                    409,
                    "Conflict",
                    "diagnostic_state_conflict",
                    ex.Message,
                    "Wait for the active diagnostic/target state to become compatible, or stop the conflicting diagnostic before retrying.",
                    keepAlive,
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (
                ex is IOException or
                TimeoutException or
                PlatformNotSupportedException or
                System.Net.Sockets.SocketException)
            {
                await WriteApiErrorAsync(
                    stream,
                    503,
                    "Service Unavailable",
                    "diagnostic_tool_failed",
                    ex.Message,
                    "Check xemu-test-runner tools and the diagnostic evidence directory for the failing tool/provider, then retry.",
                    false,
                    ct).ConfigureAwait(false);
                return false;
            }

            return true;
        }

        await WriteApiErrorAsync(
            stream,
            404,
            "Not Found",
            "route_not_found",
            $"No diagnostic route matches {request.Method} {request.Path}.",
            "Use GET /api/v1/help or GET /api/v1/diagnostics for supported diagnostic routes.",
            keepAlive,
            ct).ConfigureAwait(false);
        return true;
    }

    private sealed class DiagnosticRunRequest
    {
        public string? Id { get; set; }
        public DiagnosticRecipe? Recipe { get; set; }
    }

    private async Task<bool?> TryEvidenceRouteAsync(Stream stream, HttpRequest request, bool keepAlive, CancellationToken ct)
    {
        var diagnosticRoute = await TryDiagnosticRouteAsync(stream, request, keepAlive, ct).ConfigureAwait(false);
        if (diagnosticRoute.HasValue)
            return diagnosticRoute.Value;

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
            if (request.Method == "HEAD" &&
                !(parts.Length == 3 && parts[1] == "artifacts"))
            {
                await WriteApiErrorAsync(
                    stream,
                    405,
                    "Method Not Allowed",
                    "method_not_allowed",
                    "HEAD is only supported for evidence artifact downloads.",
                    "Use GET for run metadata/log tails, or HEAD /api/v1/runs/<run-id>/artifacts/<path> for artifact metadata.",
                    keepAlive,
                    ct).ConfigureAwait(false);
                return true;
            }
            if (parts.Length == 1)
            {
                await WriteJsonAsync(stream, 200, "OK", new { RunId = runId, Artifacts = catalog.Artifacts(runId) }, keepAlive, ct);
                return true;
            }
            if (parts[1] == "tail" && request.Method == "GET")
            {
                if (!await EnsureOperationAllowedAsync(
                        stream,
                        "bulk_transfer",
                        keepAlive,
                        ct).ConfigureAwait(false))
                    return true;

                var name = GetQueryValue(request.Query, "file") ?? "stdout.log";
                var bytes = int.TryParse(GetQueryValue(request.Query, "bytes"), out var parsed) ? parsed : 32768;
                await WriteJsonAsync(stream, 200, "OK", await catalog.TailAsync(runId, name, bytes, ct), keepAlive, ct);
                return true;
            }
            if (parts[1] == "artifacts" && parts.Length == 3)
            {
                if (!await EnsureOperationAllowedAsync(
                        stream,
                        "bulk_transfer",
                        keepAlive,
                        ct).ConfigureAwait(false))
                    return true;

                var path = catalog.Resolve(runId, Uri.UnescapeDataString(parts[2]));
                // Open before sending any headers. Downloads use the actual handle length.
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    _options.TransferBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
                FileRange range;
                try { range = FileRange.Parse(request.Headers.GetValueOrDefault("Range"), file.Length); }
                catch (InvalidDataException)
                {
                    await WriteApiErrorAsync(
                        stream,
                        416,
                        "Range Not Satisfiable",
                        "range_invalid",
                        "The requested byte range cannot be satisfied.",
                        $"Request one valid byte range within 0..{Math.Max(0, file.Length - 1)}, for example Range: bytes=0-1023.",
                        keepAlive,
                        ct,
                        new { fileLength = file.Length }).ConfigureAwait(false);
                    return true;
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
            await WriteApiErrorAsync(
                stream,
                404,
                "Not Found",
                "route_not_found",
                "Evidence route not found.",
                "Use GET /api/v1/runs, GET /api/v1/runs/<run-id>, /tail, or /artifacts/<path>. See /api/v1/help.",
                keepAlive,
                ct).ConfigureAwait(false);
            return true;
        }
        catch (FileNotFoundException e)
        {
            await WriteApiErrorAsync(
                stream,
                404,
                "Not Found",
                "evidence_not_found",
                e.Message,
                "List runs/artifacts first and retry with an existing run ID and relative artifact path.",
                false,
                ct).ConfigureAwait(false);
            return false;
        }
        catch (DirectoryNotFoundException e)
        {
            await WriteApiErrorAsync(
                stream,
                404,
                "Not Found",
                "evidence_not_found",
                e.Message,
                "List runs first with GET /api/v1/runs and use an existing run ID.",
                false,
                ct).ConfigureAwait(false);
            return false;
        }
        catch (InvalidDataException e)
        {
            await WriteApiErrorAsync(
                stream,
                400,
                "Bad Request",
                "evidence_request_invalid",
                e.Message,
                "Use a valid run ID, supported log name, and package-relative artifact path. Linked/traversal paths are not allowed.",
                false,
                ct).ConfigureAwait(false);
            return false;
        }
    }
}
