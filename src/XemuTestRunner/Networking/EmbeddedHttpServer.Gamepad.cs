using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Control.Gamepad;

namespace XemuTestRunner.Networking;

public sealed partial class EmbeddedHttpServer
{
    private sealed class ControllerStateRequest
    {
        public string SessionId { get; set; } = "";
        public ControllerStateDefinition? State { get; set; }
        public int DurationMs { get; set; }
    }

    private async Task<bool> HandleControllerStateAsync(
        Stream stream, HttpRequest request, bool keepAlive, CancellationToken ct)
    {
        if (request.ContentLength is not long length || length is < 1 or > 16384)
        {
            await WriteApiErrorAsync(stream, 400, "Bad Request", "request_body_invalid",
                "Controller state requires a JSON body of at most 16384 bytes.",
                "Send SessionId, complete State, and DurationMs between 1 and 60000.",
                false, ct).ConfigureAwait(false);
            return false;
        }
        var bytes = new byte[(int)length];
        await ReadExactlyAsync(stream, bytes, ct).ConfigureAwait(false);
        try
        {
            var command = JsonSerializer.Deserialize<ControllerStateRequest>(bytes, ConfigLoader.JsonOptions)
                ?? throw new InvalidDataException("Empty controller state request.");
            if (command.State is null || command.SessionId.Length != 32 ||
                command.DurationMs is < 1 or > 60000)
                throw new InvalidDataException("Controller state requires SessionId, State, and DurationMs 1..60000.");
            _ = command.State.ToState();
            await _control.PressControllerStateAsync(command.State, command.DurationMs,
                command.SessionId, ct).ConfigureAwait(false);
            Activity.Mark("manual_input", new { kind = "controller_state",
                command.SessionId, command.DurationMs });
            await WriteJsonAsync(stream, 200, "OK", new { accepted = true,
                sessionId = command.SessionId, durationMs = command.DurationMs },
                keepAlive, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            await WriteApiErrorAsync(stream, 400, "Bad Request", "request_body_invalid",
                error.Message, "Use valid named buttons and bounded analog values.",
                keepAlive, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException error)
        {
            await WriteApiErrorAsync(stream, 409, "Conflict", "controller_session_unavailable",
                error.Message, "Refresh GET /api/v1/control and retry only while a native controller test is idle.",
                keepAlive, ct).ConfigureAwait(false);
        }
        return true;
    }
}
