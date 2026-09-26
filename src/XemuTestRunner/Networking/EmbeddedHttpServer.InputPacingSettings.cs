namespace XemuTestRunner.Networking;

public sealed partial class EmbeddedHttpServer
{
    private readonly object _inputPacingSettingsGate = new();
    private InputPacingSettingsStore? _inputPacingSettings;

    private InputPacingSettingsStore InputPacingSettings
    {
        get
        {
            lock (_inputPacingSettingsGate)
                return _inputPacingSettings ??=
                    new InputPacingSettingsStore(_paths.Workspace);
        }
    }

    private async Task<bool?> TryInputPacingSettingsRoutesAsync(
        Stream stream,
        HttpRequest request,
        CancellationToken ct)
    {
        if (request.Method == "GET" &&
            request.Path == "/settings/input-pacing")
        {
            await WriteHtmlAsync(
                stream,
                InputPacingSettingsPage.Html,
                false,
                ct).ConfigureAwait(false);
            return false;
        }

        if (request.Method == "GET" && request.Path == "/api/v1/settings")
        {
            await WriteAgentJsonAsync(
                stream,
                new { inputPacing = InputPacingSettings.Read() },
                cancellationToken: ct).ConfigureAwait(false);
            return false;
        }

        if (request.Path != "/api/v1/settings/input-pacing")
            return null;

        if (request.Method == "GET")
        {
            await WriteAgentJsonAsync(
                stream,
                InputPacingSettings.Read(),
                cancellationToken: ct).ConfigureAwait(false);
            return false;
        }

        if (request.Method != "PUT")
            return null;

        InputPacingSettingsUpdateRequest update;
        try
        {
            update = await ReadAgentBodyAsync<InputPacingSettingsUpdateRequest>(
                stream,
                request,
                ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException)
        {
            await WriteApiErrorAsync(
                stream,
                400,
                "Bad Request",
                "input_pacing_settings_invalid",
                error.Message,
                "Send the complete bounded settings document returned by GET, including expectedRevision.",
                false,
                ct).ConfigureAwait(false);
            return false;
        }

        try
        {
            var saved = InputPacingSettings.Update(
                update.ExpectedRevision,
                update.Settings);
            await WriteAgentJsonAsync(
                stream,
                saved,
                revision: $"\"{saved.Revision}\"",
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (InputPacingSettingsRevisionException error)
        {
            await WriteApiErrorAsync(
                stream,
                409,
                "Conflict",
                "settings_revision_conflict",
                error.Message,
                "Read the current settings and deliberately reapply the update to that revision.",
                false,
                ct,
                new { error.Expected, error.Actual }).ConfigureAwait(false);
        }
        catch (InvalidDataException error)
        {
            await WriteApiErrorAsync(
                stream,
                400,
                "Bad Request",
                "input_pacing_settings_invalid",
                error.Message,
                "Keep interval values nonnegative and ensure each maximum covers its minimum and enabled fallback.",
                false,
                ct).ConfigureAwait(false);
        }

        return false;
    }
}
