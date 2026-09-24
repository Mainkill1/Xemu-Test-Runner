using System.Text.Json;

namespace XemuTestRunner.Networking;

internal sealed record RecordedEmulatorIdentity(string? Sha256, string? LaunchSha256, string? Code)
{
    // Reporting uses recorded facts only. A wrapper hash must not silently
    // stand in for an unverified native payload. Indexing applies the stronger
    // immutable package/validation checks in AgentJobStore as well.
    public static RecordedEmulatorIdentity Read(JsonElement manifest)
    {
        var launch = Text(manifest, "ExecutableSha256");
        if (!IsHash(launch)) launch = null;
        if (!manifest.TryGetProperty("Inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Array)
            return new(launch, launch, launch is null ? "executable_identity_missing" : null);
        var declared = inputs.EnumerateArray().Where(input =>
            Text(input, "Role")?.Equals("emulator", StringComparison.OrdinalIgnoreCase) == true).ToArray();
        if (declared.Length == 0)
            declared = inputs.EnumerateArray().Where(input =>
            {
                var name = Path.GetFileName(Text(input, "Path")?.Replace('\\', '/') ?? "");
                return name.Equals("xemu", StringComparison.OrdinalIgnoreCase) || name.Equals("xemu.exe", StringComparison.OrdinalIgnoreCase);
            }).ToArray();
        if (declared.Length == 0) return new(launch, launch, launch is null ? "executable_identity_missing" : null);
        if (declared.Length != 1) return new(null, launch, "emulator_identity_ambiguous");
        var selected = declared[0];
        var sha = Text(selected, "Sha256");
        if (IsHash(sha) && string.Equals(sha, launch, StringComparison.OrdinalIgnoreCase)) return new(launch, launch, null);
        if (!IsHash(sha) || !string.Equals(sha, Text(selected, "ExpectedSha256"), StringComparison.OrdinalIgnoreCase) ||
            !selected.TryGetProperty("Verified", out var verified) || verified.ValueKind != JsonValueKind.True)
            return new(null, launch, "emulator_identity_unverified");
        return new(sha!.ToLowerInvariant(), launch, null);
    }
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static string? Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
