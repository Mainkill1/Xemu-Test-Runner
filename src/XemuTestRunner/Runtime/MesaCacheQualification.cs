using System.Text.Json;

namespace XemuTestRunner.Runtime;

/// <summary>Post-exit evidence of writes to a fresh Mesa disk namespace, not a GPU/OS cache reset.</summary>
public static class MesaCacheQualification
{
    public const string Contract = "mesa-multifile-disk-namespace-v1";
    public const string Verified = "mesa_private_disk_writes_observed";

    public static string Evaluate(RunStorageReport report, string expectedDirectory)
    {
        if (report.Schema != 2 || report.DriverCache != "mesa") return "mesa_adapter_not_qualified";
        if (!report.TargetStopped || report.Status != "complete") return "mesa_final_state_incomplete";
        if (report.DriverCacheDirectory is null || !MatchesDirectory(report.DriverCacheDirectory, expectedDirectory) ||
            report.CacheEnvironment.GetValueOrDefault("effective:MESA_SHADER_CACHE_DIR") != report.DriverCacheDirectory ||
            report.CacheEnvironment.GetValueOrDefault("effective:MESA_SHADER_CACHE_DISABLE") != "false" ||
            report.CacheEnvironment.GetValueOrDefault("effective:MESA_SHADER_CACHE_MAX_SIZE") != "256M")
            return "mesa_environment_mismatch";
        var expectedKeys = new[] { "effective:MESA_SHADER_CACHE_DIR", "effective:MESA_SHADER_CACHE_DISABLE", "effective:MESA_SHADER_CACHE_MAX_SIZE" };
        if (report.CacheEnvironment.Keys.Any(key => key.StartsWith("effective:", StringComparison.Ordinal) && !expectedKeys.Contains(key)))
            return "mesa_cache_override_unqualified";
        if (!ValidSnapshot(report.DriverBefore) || report.DriverBefore!.Files.Count != 0)
            return "mesa_initial_namespace_not_empty";
        if (!ValidSnapshot(report.DriverAfter)) return "mesa_final_inventory_incomplete";
        // Mesa's multi-file backend uses a 40-hex-character key, split 2/38.
        // Index files and RADV empty database headers are not cache-data proof.
        if (!report.DriverAfter!.Files.Any(IsDataEntry)) return "mesa_no_recognized_disk_writes";
        return Verified;
    }

    private static bool MatchesDirectory(string actual, string expected)
    {
        try { return Path.IsPathFullyQualified(actual) && Path.GetFullPath(actual).Equals(Path.GetFullPath(expected), StringComparison.Ordinal); }
        catch (ArgumentException) { return false; }
    }

    private static bool IsDataEntry(RunStateFile file)
    {
        var parts = file.Path.Split('/');
        return file.Bytes > 0 && parts.Length == 3 && parts[0] == "mesa_shader_cache" &&
            parts[1].Length == 2 && parts[2].Length == 38 && parts[1].All(Uri.IsHexDigit) && parts[2].All(Uri.IsHexDigit);
    }

    private static bool ValidSnapshot(RunStateSnapshot? snapshot) => snapshot is { Complete: true } &&
        snapshot.Bytes is >= 0 and <= 2147483648L && snapshot.Issues is not null && snapshot.Issues.Count == 0 && snapshot.Files is not null && snapshot.Files.Count <= 16384 &&
        snapshot.Files.All(file => file is not null && file.Path is not null && file.Bytes is >= 0 and <= 2147483648L) && snapshot.Files.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() == snapshot.Files.Count &&
        snapshot.Files.All(file => file.Bytes >= 0 && !Path.IsPathRooted(file.Path) && !file.Path.Contains('\\') &&
            file.Path.Split('/').All(part => part is not ("" or "." or ".."))) &&
        snapshot.Bytes == snapshot.Files.Sum(file => file.Bytes) &&
        snapshot.TreeSha256 == RunStateInventory.Hash(JsonSerializer.SerializeToUtf8Bytes(snapshot.Files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray()));
}
