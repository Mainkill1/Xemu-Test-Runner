using System.Text.Json;
using System.Text.Json.Serialization;
using XemuTestRunner.Config;

namespace XemuTestRunner.Networking;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InputPacingIntervalSettings
{
    public int MinimumMs { get; init; }
    public int MinimumFrames { get; init; }
    public int FallbackMs { get; init; }
    public int MaximumMs { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InputPacingSettings
{
    public bool Enabled { get; init; }
    public bool AllowTimeOnlyFallback { get; init; } = true;
    public InputPacingIntervalSettings Preparation { get; init; } = new()
    {
        MinimumMs = 0,
        MinimumFrames = 0,
        FallbackMs = 0,
        MaximumMs = 15000
    };
    public InputPacingIntervalSettings Hold { get; init; } = new()
    {
        MinimumMs = 250,
        MinimumFrames = 15,
        FallbackMs = 1000,
        MaximumMs = 5000
    };
    public InputPacingIntervalSettings Neutral { get; init; } = new()
    {
        MinimumMs = 250,
        MinimumFrames = 1,
        FallbackMs = 250,
        MaximumMs = 5000
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record InputPacingSettingsUpdateRequest(
    int ExpectedRevision,
    InputPacingSettings Settings);

internal sealed record InputPacingSettingsDocument(
    int Revision,
    DateTimeOffset? UpdatedUtc,
    InputPacingSettings Settings);

internal sealed record InputPacingSettingsCapabilities(
    bool FramePacingAvailable,
    string FrameProvider);

internal sealed record InputPacingSettingsView(
    int Revision,
    DateTimeOffset? UpdatedUtc,
    InputPacingSettings Settings,
    InputPacingSettingsCapabilities Capabilities,
    string EffectiveMode);

internal sealed class InputPacingSettingsRevisionException : Exception
{
    public InputPacingSettingsRevisionException(int expected, int actual)
        : base($"Input-pacing settings revision {expected} is stale; current revision is {actual}.")
    {
        Expected = expected;
        Actual = actual;
    }

    public int Expected { get; }
    public int Actual { get; }
}

internal sealed class InputPacingSettingsStore
{
    private const int InitialRevision = 1;
    private const int MaximumIntervalMs = 300000;
    private const int MaximumFrames = 10000;
    private static readonly JsonSerializerOptions PersistJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly string _path;
    private InputPacingSettingsDocument _document;

    public InputPacingSettingsStore(string workspace)
    {
        _path = Path.Combine(workspace, ".settings", "input-pacing.json");
        _document = Load();
    }

    public InputPacingSettingsView Read()
    {
        lock (_gate)
            return View(_document);
    }

    public InputPacingSettingsView Update(int expectedRevision, InputPacingSettings settings)
    {
        if (settings is null)
            throw new InvalidDataException("Input-pacing settings are required.");
        Validate(settings);

        lock (_gate)
        {
            if (expectedRevision != _document.Revision)
                throw new InputPacingSettingsRevisionException(expectedRevision, _document.Revision);

            var updated = new InputPacingSettingsDocument(
                checked(_document.Revision + 1),
                DateTimeOffset.UtcNow,
                settings);
            Persist(updated);
            _document = updated;
            return View(updated);
        }
    }

    private InputPacingSettingsDocument Load()
    {
        if (!File.Exists(_path))
            return new InputPacingSettingsDocument(
                InitialRevision,
                null,
                new InputPacingSettings());

        InputPacingSettingsDocument document;
        try
        {
            document = JsonSerializer.Deserialize<InputPacingSettingsDocument>(
                File.ReadAllText(_path),
                ConfigLoader.JsonOptions)
                ?? throw new InvalidDataException("Input-pacing settings file is empty.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException(
                "Input-pacing settings file is invalid: " + error.Message,
                error);
        }

        if (document.Revision < InitialRevision)
            throw new InvalidDataException("Input-pacing settings revision must be positive.");
        if (document.Settings is null)
            throw new InvalidDataException("Input-pacing settings are missing.");
        Validate(document.Settings);
        return document;
    }

    private void Persist(InputPacingSettingsDocument document)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(document, PersistJson));
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static InputPacingSettingsView View(InputPacingSettingsDocument document)
    {
        const bool framePacingAvailable = false;
        var effectiveMode = !document.Settings.Enabled
            ? "off"
            : framePacingAvailable
                ? "hybrid"
                : document.Settings.AllowTimeOnlyFallback
                    ? "timeOnlyFallback"
                    : "unavailable";

        return new InputPacingSettingsView(
            document.Revision,
            document.UpdatedUtc,
            document.Settings,
            new InputPacingSettingsCapabilities(
                framePacingAvailable,
                "unavailable"),
            effectiveMode);
    }

    private static void Validate(InputPacingSettings settings)
    {
        if (settings.Preparation is null || settings.Hold is null || settings.Neutral is null)
            throw new InvalidDataException(
                "Preparation, hold, and neutral input-pacing intervals are required.");

        ValidateInterval("Preparation", settings.Preparation, settings.AllowTimeOnlyFallback);
        ValidateInterval("Hold", settings.Hold, settings.AllowTimeOnlyFallback);
        ValidateInterval("Neutral", settings.Neutral, settings.AllowTimeOnlyFallback);
    }

    private static void ValidateInterval(
        string name,
        InputPacingIntervalSettings interval,
        bool allowFallback)
    {
        if (interval.MinimumMs is < 0 or > MaximumIntervalMs)
            throw new InvalidDataException(
                $"{name}.MinimumMs must be between 0 and {MaximumIntervalMs}.");
        if (interval.MinimumFrames is < 0 or > MaximumFrames)
            throw new InvalidDataException(
                $"{name}.MinimumFrames must be between 0 and {MaximumFrames}.");
        if (interval.FallbackMs is < 0 or > MaximumIntervalMs)
            throw new InvalidDataException(
                $"{name}.FallbackMs must be between 0 and {MaximumIntervalMs}.");
        if (interval.MaximumMs is < 1 or > MaximumIntervalMs)
            throw new InvalidDataException(
                $"{name}.MaximumMs must be between 1 and {MaximumIntervalMs}.");
        if (interval.MaximumMs < interval.MinimumMs)
            throw new InvalidDataException(
                $"{name}.MaximumMs cannot be less than MinimumMs.");
        if (allowFallback && interval.MaximumMs < interval.FallbackMs)
            throw new InvalidDataException(
                $"{name}.MaximumMs cannot be less than FallbackMs while time-only fallback is enabled.");
    }
}
