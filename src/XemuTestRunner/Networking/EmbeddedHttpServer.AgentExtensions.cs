using System.Text.Json;

namespace XemuTestRunner.Networking;

public sealed partial class EmbeddedHttpServer
{
    // Keep additive agent routes separate from the established package/transfer
    // protocol. All extensions retain the same error and connection contract.
    private async Task<bool?> TryAgentExtensionRouteAsync(Stream stream, HttpRequest request, CancellationToken ct)
    {
        try
        {
            var observation = await TryObservationRouteAsync(stream, request, ct).ConfigureAwait(false);
            if (observation.HasValue) return observation.Value;
            return null;
        }
        catch (AgentRequestException error)
        {
            await WriteApiErrorAsync(stream, error.Status, AgentReason(error.Status), error.Code,
                error.Message, error.Hint, false, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
        {
            await WriteApiErrorAsync(stream, 400, "Bad Request", "request_invalid", error.Message,
                "Check the requested view, IDs and bounded query parameters.", false, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            await WriteApiErrorAsync(stream, 409, "Conflict", "agent_io_error", error.Message,
                "Inspect the existing job/run before retrying. Do not create a duplicate attempt.", false, ct).ConfigureAwait(false);
        }
        return false;
    }
}
