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
            await WriteApiErrorAsync(
                stream,
                404,
                "Not Found",
                "preview_disabled",
                "Live preview is disabled by runner configuration.",
                "Enable Ui.LivePreviewEnabled in runner.json, or use retained screenshots/other evidence instead.",
                keepAlive,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!_control.HasActiveSession)
        {
            await WriteApiErrorAsync(
                stream,
                409,
                "Conflict",
                "target_not_active",
                "No xemu test is currently active.",
                "Start a queued test before requesting a live preview.",
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

            await WriteApiErrorAsync(
                stream,
                503,
                "Service Unavailable",
                "preview_unavailable",
                ex.Message,
                "Verify xemu is running and the configured screenshot provider can capture a frame. Check /api/v1/control and the current run evidence.",
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
            await WriteApiErrorAsync(
                stream,
                503,
                "Service Unavailable",
                "pause_failed",
                ex.Message,
                "Verify an active QMP-controlled xemu target exists and retry. Check /api/v1/control for current state.",
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
            await WriteApiErrorAsync(
                stream,
                503,
                "Service Unavailable",
                "resume_failed",
                ex.Message,
                "Verify an active QMP-controlled xemu target exists and retry. Check /api/v1/control for current state.",
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
            await WriteApiErrorAsync(
                stream,
                409,
                "Conflict",
                "recorder_state_invalid",
                ex.Message,
                "Check GET /api/v1/input/record for the current recorder state before starting/stopping/clearing it.",
                keepAlive,
                cancellationToken).ConfigureAwait(false);
        }
    }
}
