using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Queue;

namespace XemuTestRunner.Runtime;

public static class RunStateQualification
{
    public static RunStorageReport? Read(string resultDirectory)
    {
        var path = Path.Combine(resultDirectory, "diagnostics", "run-state", "report.json");
        RunStateInventory.NoLinks(path);
        if (!File.Exists(path)) return null;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length > 8 * 1024 * 1024) throw new InvalidDataException("State report exceeds 8 MiB.");
        using var document = JsonDocument.Parse(input);
        var root = document.RootElement;
        foreach (var name in new[] { "Schema", "Status", "CacheMode", "ComparisonReady", "Uncontrolled", "Issues" })
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out _))
                throw new InvalidDataException("State report is missing required fields.");
        var report = root.Deserialize<RunStorageReport>(ConfigLoader.JsonOptions) ?? throw new InvalidDataException("Empty state report.");
        if (report.Schema != 1 || report.Uncontrolled is null || report.Issues is null || report.CacheEnvironment is null || report.StoragePaths is null ||
            report.CacheMode is not ("cold" or "seeded" or "inherited" or "unmanaged") ||
            report.Status is not ("preparing" or "prepared" or "complete" or "incomplete" or "unmanaged" or "preparationFailed" or "targetNotStopped") ||
            report.Uncontrolled.Concat(report.Issues).Any(value => value is null || value.Length > 256))
            throw new InvalidDataException("State report has invalid fields.");
        foreach (var snapshot in new[] { report.Before, report.After, report.DriverAfter })
        {
            if (snapshot is null) continue;
            if (snapshot.Files is null || snapshot.Issues is null || snapshot.Files.Count > 16384 || snapshot.Bytes < 0 ||
                snapshot.TreeSha256 is null || snapshot.TreeSha256.Length != 64 || !snapshot.TreeSha256.All(Uri.IsHexDigit) ||
                snapshot.Files.Any(file => file is null || file.Path is null || file.Path.Length > 512 || file.Bytes < 0 ||
                    file.Sha256 is null || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit)))
                throw new InvalidDataException("Invalid state inventory.");
        }
        if (report.ComparisonReady && (report.ContractSha256 is null || report.ContractSha256.Length != 64 ||
            !report.ContractSha256.All(Uri.IsHexDigit) || report.Before?.Complete != true || report.After?.Complete != true ||
            !report.TargetStopped || report.Status != "complete" || report.CacheMode is "unmanaged" or "inherited" || !report.AllowUncontrolledDriverCache))
            throw new InvalidDataException("State report cannot substantiate comparison readiness.");
        return report;
    }

    public static AssessmentCheck? Check(JobDefinition job, string resultDirectory)
    {
        if (job.RuntimeState.Isolation is null && !job.Operations.IsBenchmark) return null;
        try
        {
            var report = Read(resultDirectory);
            var policy = job.RuntimeState.Isolation;
            var passed = policy is not null && report?.ComparisonReady == true &&
                report.CacheMode == policy.CacheMode && report.CacheShaders == policy.CacheShaders &&
                report.DriverCache == policy.DriverCache && report.AllowUncontrolledDriverCache == policy.AllowUncontrolledDriverCache &&
                (policy.CacheMode != "seeded" || string.Equals(policy.SeedSha256, report.SeedSha256, StringComparison.OrdinalIgnoreCase));
            return new("run_storage_state", passed, "state", passed
                ? "Application cache and private guest inputs recorded; driver and OS caches remain explicitly disclosed limitations."
                : "State is unmanaged, incomplete, inherited, inconsistent with its policy, or lacks explicit acceptance of uncontrolled driver caching. Read the run state summary.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        { return new("run_storage_state", false, "state", "Run-state evidence is unavailable or invalid."); }
    }

    public static string ComparisonKey(string procedureKey, RunStorageReport? report, bool managed)
    {
        if (!managed) return procedureKey;
        return RunStateInventory.Hash(JsonSerializer.SerializeToUtf8Bytes(new
        { procedureKey, stateContract = report?.ContractSha256 ?? "state-evidence-missing" }));
    }
}
