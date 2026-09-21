using System.Net.Sockets;

namespace XemuTestRunner.Networking;

public sealed partial class EmbeddedHttpServer
{
    private async Task HandlePreviewAsync(
        Stream stream,
        bool keepAlive,
        CancellationToken cancellationToken)
    {
        if (!_uiOptions.LivePreviewEnabled)
        {
            await WriteJsonAsync(
                stream,
                404,
                "Not Found",
                new { error = "Live preview is disabled by configuration." },
                keepAlive,
                cancellationToken).ConfigureAwait(false);
            return;
        }

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

        string? path = null;
        var responseStarted = false;

        try
        {
            path = await _control.CapturePreviewAsync(cancellationToken).ConfigureAwait(false);
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
                ["Cache-Control"] = "no-store, no-cache, must-revalidate",
                ["Pragma"] = "no-cache"
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
                throw new IOException("Preview response failed after HTTP headers were sent.", ex);

            await WriteJsonAsync(
                stream,
                503,
                "Service Unavailable",
                new { error = ex.Message },
                false,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                try { File.Delete(path); } catch { }
            }
        }
    }

    private async Task HandlePauseAsync(
        Stream stream,
        HttpRequest request,
        bool keepAlive,
        CancellationToken cancellationToken)
    {
        if ((request.ContentLength ?? 0) > 0)
            await DrainBodyAsync(stream, request.ContentLength!.Value, cancellationToken).ConfigureAwait(false);

        try
        {
            await _control.PauseAsync(cancellationToken).ConfigureAwait(false);
            _state.SetPhase("paused");

            await WriteJsonAsync(
                stream,
                200,
                "OK",
                _control.Snapshot(),
                keepAlive,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is IOException or
            SocketException or
            InvalidOperationException or
            TimeoutException)
        {
            await WriteJsonAsync(
                stream,
                503,
                "Service Unavailable",
                new { error = ex.Message },
                keepAlive,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleResumeAsync(
        Stream stream,
        HttpRequest request,
        bool keepAlive,
        CancellationToken cancellationToken)
    {
        if ((request.ContentLength ?? 0) > 0)
            await DrainBodyAsync(stream, request.ContentLength!.Value, cancellationToken).ConfigureAwait(false);

        try
        {
            await _control.ResumeAsync(cancellationToken).ConfigureAwait(false);
            _state.SetPhase("running");

            await WriteJsonAsync(
                stream,
                200,
                "OK",
                _control.Snapshot(),
                keepAlive,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is IOException or
            SocketException or
            InvalidOperationException or
            TimeoutException)
        {
            await WriteJsonAsync(
                stream,
                503,
                "Service Unavailable",
                new { error = ex.Message },
                keepAlive,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleRecordingActionAsync(
        Stream stream,
        HttpRequest request,
        bool keepAlive,
        string action,
        CancellationToken cancellationToken)
    {
        if ((request.ContentLength ?? 0) > 0)
            await DrainBodyAsync(stream, request.ContentLength!.Value, cancellationToken).ConfigureAwait(false);

        try
        {
            var result = action switch
            {
                "start" => _control.StartRecording(),
                "stop" => _control.StopRecording(),
                "clear" => _control.ClearRecording(),
                _ => throw new InvalidDataException($"Unknown recorder action '{action}'.")
            };

            await WriteJsonAsync(
                stream,
                200,
                "OK",
                result,
                keepAlive,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            await WriteJsonAsync(
                stream,
                409,
                "Conflict",
                new { error = ex.Message },
                keepAlive,
                cancellationToken).ConfigureAwait(false);
        }
    }
}
