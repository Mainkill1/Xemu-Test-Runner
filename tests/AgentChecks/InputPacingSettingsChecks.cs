using System.Net;
using System.Text.Json;

internal static class InputPacingSettingsChecks
{
    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("input pacing settings defaults and UI", DefaultsAndUiAsync));
        checks.Add(("input pacing settings update and persistence", UpdateAndPersistenceAsync));
        checks.Add(("input pacing settings validation", ValidationAsync));
    }

    private static async Task DefaultsAndUiAsync()
    {
        await using var fixture = new AgentFixture();

        var current = await fixture.Json("/api/v1/settings/input-pacing");
        var settings = current.GetProperty("settings");
        AgentFixture.Require(!settings.GetProperty("enabled").GetBoolean(),
            "Input pacing must remain disabled by default.");
        AgentFixture.Require(settings.GetProperty("hold").GetProperty("minimumMs").GetInt32() == 250,
            "The default hybrid hold minimum must be 250 ms.");
        AgentFixture.Require(settings.GetProperty("hold").GetProperty("minimumFrames").GetInt32() == 15,
            "The default hybrid hold minimum must be 15 frames.");
        AgentFixture.Require(current.GetProperty("effectiveMode").GetString() == "off",
            "A disabled profile must report effective mode off.");
        AgentFixture.Require(!current.GetProperty("capabilities").GetProperty("framePacingAvailable").GetBoolean(),
            "The current runner must not invent a frame provider.");

        var summary = await fixture.Json("/api/v1/settings");
        AgentFixture.Require(summary.GetProperty("inputPacing").GetProperty("revision").GetInt32() ==
            current.GetProperty("revision").GetInt32(),
            "The settings summary must expose the same input-pacing revision.");

        var html = await fixture.Client.GetStringAsync("/settings/input-pacing");
        AgentFixture.Require(html.Contains("input-pacing-enabled", StringComparison.Ordinal),
            "The settings page must expose the pacing toggle.");
        AgentFixture.Require(html.Contains("/api/v1/settings/input-pacing", StringComparison.Ordinal),
            "The settings page must call the input-pacing API.");
        AgentFixture.Require(html.Contains("minimumFrames", StringComparison.Ordinal),
            "The settings page must expose frame requirements.");
        AgentFixture.Require(html.Contains("fallbackMs", StringComparison.Ordinal),
            "The settings page must expose time-only fallback values.");
    }

    private static async Task UpdateAndPersistenceAsync()
    {
        await using var fixture = new AgentFixture();
        var before = await fixture.Json("/api/v1/settings/input-pacing");
        var revision = before.GetProperty("revision").GetInt32();

        var update = SettingsRequest(revision, enabled: true, holdFrames: 15, holdFallbackMs: 1250);
        var saved = await fixture.Json(
            "/api/v1/settings/input-pacing",
            HttpMethod.Put,
            update);

        AgentFixture.Require(saved.GetProperty("revision").GetInt32() == revision + 1,
            "A successful settings write must advance the revision.");
        AgentFixture.Require(saved.GetProperty("settings").GetProperty("enabled").GetBoolean(),
            "The pacing toggle was not saved.");
        AgentFixture.Require(saved.GetProperty("effectiveMode").GetString() == "timeOnlyFallback",
            "Without a frame provider, enabled pacing must report the configured fallback mode.");

        var readback = await fixture.Json("/api/v1/settings/input-pacing");
        AgentFixture.Require(readback.GetProperty("settings").GetProperty("hold")
                .GetProperty("fallbackMs").GetInt32() == 1250,
            "GET did not return the saved fallback value.");

        var persisted = Path.Combine(fixture.Paths.Workspace, ".settings", "input-pacing.json");
        AgentFixture.Require(File.Exists(persisted),
            "A successful API update must persist runner-owned settings atomically.");
        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(persisted)))
        {
            AgentFixture.Require(document.RootElement.GetProperty("settings").GetProperty("enabled").GetBoolean(),
                "The persisted settings file did not retain the enabled toggle.");
        }

        var stale = await fixture.Json(
            "/api/v1/settings/input-pacing",
            HttpMethod.Put,
            SettingsRequest(revision, enabled: false, holdFrames: 15, holdFallbackMs: 1000),
            HttpStatusCode.Conflict);
        AgentFixture.Require(stale.GetProperty("code").GetString() ==
            "settings_revision_conflict",
            "A stale writer must receive a revision conflict rather than overwrite newer settings.");
    }

    private static async Task ValidationAsync()
    {
        await using var fixture = new AgentFixture();
        var current = await fixture.Json("/api/v1/settings/input-pacing");
        var revision = current.GetProperty("revision").GetInt32();

        var invalid = new
        {
            expectedRevision = revision,
            settings = new
            {
                enabled = true,
                allowTimeOnlyFallback = true,
                preparation = Interval(0, 0, 0, 15000),
                hold = Interval(5000, 15, 1000, 250),
                neutral = Interval(250, 1, 250, 5000)
            }
        };
        var response = await fixture.Json(
            "/api/v1/settings/input-pacing",
            HttpMethod.Put,
            invalid,
            HttpStatusCode.BadRequest);
        AgentFixture.Require(response.GetProperty("code").GetString() ==
            "input_pacing_settings_invalid",
            "Invalid pacing bounds need a stable API error code.");

        var after = await fixture.Json("/api/v1/settings/input-pacing");
        AgentFixture.Require(after.GetProperty("revision").GetInt32() == revision,
            "A rejected update must not advance the settings revision.");
    }

    private static object SettingsRequest(int revision, bool enabled, int holdFrames, int holdFallbackMs) => new
    {
        expectedRevision = revision,
        settings = new
        {
            enabled,
            allowTimeOnlyFallback = true,
            preparation = Interval(0, 0, 0, 15000),
            hold = Interval(250, holdFrames, holdFallbackMs, 5000),
            neutral = Interval(250, 1, 250, 5000)
        }
    };

    private static object Interval(int minimumMs, int minimumFrames, int fallbackMs, int maximumMs) => new
    {
        minimumMs,
        minimumFrames,
        fallbackMs,
        maximumMs
    };
}
