using System.Text.Json;

namespace XemuTestRunner.Networking;

public sealed partial class EmbeddedHttpServer
{
    private async Task<bool?> TryAgentExtensionRouteAsync(Stream stream, HttpRequest request, CancellationToken ct)
    {
        try
        {
            // This serves only a static viewer shell. Evidence bytes still use
            // the existing ranged artifact endpoint and its operation policy.
            if (request.Method == "GET" && request.Path == "/results/view")
            {
                await WriteHtmlAsync(stream, ArtifactViewerPage.Html, false, ct).ConfigureAwait(false);
                return false;
            }
            await ConsumeAgentActionBodyAsync(stream, request, ct).ConfigureAwait(false);
            var xiso = await TryXisoRoutesAsync(stream, request, ct).ConfigureAwait(false);
            if (xiso.HasValue) return xiso.Value;
            var application = await TryApplicationIdentityRouteAsync(stream, request, ct).ConfigureAwait(false);
            if (application.HasValue) return application.Value;
            var performance = await TryPerformanceRouteAsync(stream, request, ct).ConfigureAwait(false);
            if (performance.HasValue) return performance.Value;
            var disks = await TryDiskAssetRoutesAsync(stream, request, ct).ConfigureAwait(false);
            if (disks.HasValue) return disks.Value;
            var state = await TryRunStateRouteAsync(stream, request, ct).ConfigureAwait(false);
            if (state.HasValue) return state.Value;
            var crash = await TryCrashRoutesAsync(stream, request, ct).ConfigureAwait(false);
            if (crash.HasValue) return crash.Value;
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
        catch (AgentRequestException error)
        { await WriteApiErrorAsync(stream, error.Status, AgentReason(error.Status), error.Code, error.Message, error.Hint, false, ct).ConfigureAwait(false); }
        catch (UploadFailure error)
        { await WriteApiErrorAsync(stream, error.Status, AgentReason(error.Status), error.Code, error.Message, error.Hint, false, ct, error.Details).ConfigureAwait(false); }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or KeyNotFoundException or InvalidOperationException)
        { await WriteApiErrorAsync(stream, 400, "Bad Request", "request_invalid", error.Message, "Check IDs, revision and bounded fields. Missing evidence does not imply a pass.", false, ct).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { await WriteApiErrorAsync(stream, 409, "Conflict", "agent_io_error", error.Message, "Inspect the existing job/run before retrying. Do not create a duplicate attempt.", false, ct).ConfigureAwait(false); }
        return false;
    }
}
