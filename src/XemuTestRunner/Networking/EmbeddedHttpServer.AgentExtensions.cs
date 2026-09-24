using System.Text.Json;

namespace XemuTestRunner.Networking;

public sealed partial class EmbeddedHttpServer
{
    private async Task<bool?> TryAgentExtensionRouteAsync(Stream stream, HttpRequest request, CancellationToken ct)
    {
        BeginApiResponse(stream);
        try
        {
            ApiRequestValidation.Validate(request);
            await ConsumeAgentActionBodyAsync(stream, request, ct).ConfigureAwait(false);
            var results = await TryBuildResultRoutesAsync(stream, request, ct).ConfigureAwait(false);
            if (results.HasValue) return results.Value;
            var tests = await TryRequestedTestRoutesAsync(stream, request, ct).ConfigureAwait(false);
            if (tests.HasValue) return tests.Value;
            var evidence = await TryFocusedEvidenceRouteAsync(stream, request, ct).ConfigureAwait(false);
            if (evidence.HasValue) return evidence.Value;
            var library = await TryTestLibraryRouteAsync(stream, request, ct).ConfigureAwait(false);
            if (library.HasValue) return library.Value;
            var observation = await TryObservationRouteAsync(stream, request, ct).ConfigureAwait(false);
            if (observation.HasValue) return observation.Value;
            return null;
        }
        catch (Exception) when (ResponseHasStarted(stream)) { throw; }
        catch (ApiInputException error)
        {
            await WriteInputErrorAsync(stream, error, ct).ConfigureAwait(false);
        }
        catch (AgentRequestException error)
        {
            await WriteApiErrorAsync(stream, error.Status, AgentReason(error.Status), error.Code,
                error.Message, error.Hint, false, ct).ConfigureAwait(false);
        }
        catch (UploadFailure error)
        {
            await WriteApiErrorAsync(stream, error.Status, AgentReason(error.Status), error.Code,
                error.Message, error.Hint, false, ct, error.Details).ConfigureAwait(false);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            await WriteApiErrorAsync(stream, 400, "Bad Request", "request_invalid", error.Message,
                "Check the requested IDs, revision, fields and bounded query parameters. Missing result metadata does not imply a pass.", false, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            await WriteApiErrorAsync(stream, 409, "Conflict", "agent_io_error", error.Message,
                "Inspect the existing job/run before retrying. Do not create a duplicate attempt.", false, ct).ConfigureAwait(false);
        }
        return false;
    }
}
