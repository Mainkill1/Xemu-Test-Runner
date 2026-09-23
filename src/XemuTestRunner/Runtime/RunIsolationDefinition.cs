using System.Text.Json.Serialization;

namespace XemuTestRunner.Runtime;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RunIsolationDefinition : IJsonOnDeserialized
{
    public string CacheMode { get; set; } = "cold";
    public bool CacheShaders { get; set; } = true;
    public string DriverCache { get; set; } = "uncontrolled";
    public bool AllowUncontrolledDriverCache { get; set; }
    public string? SeedDirectory { get; set; }
    public string? SeedSha256 { get; set; }
    public bool RequirePrivateGuestState { get; set; } = true;
    public int MaximumFiles { get; set; } = 4096;
    public long MaximumBytes { get; set; } = 256L * 1024 * 1024;
    public int DeadlineSeconds { get; set; } = 15;

    void IJsonOnDeserialized.OnDeserialized() => Validate();

    public void Validate()
    {
        if (CacheMode is not ("cold" or "seeded" or "inherited"))
            throw new InvalidDataException("Isolation.CacheMode must be cold, seeded or inherited.");
        if (DriverCache is not ("uncontrolled" or "mesa" or "nvidia-gl"))
            throw new InvalidDataException("Isolation.DriverCache must be uncontrolled, mesa or nvidia-gl.");
        if (CacheMode == "seeded")
        {
            if (string.IsNullOrWhiteSpace(SeedDirectory) || Path.IsPathRooted(SeedDirectory) ||
                SeedDirectory.Contains('\\') || SeedDirectory.Contains(':') ||
                SeedDirectory.Split('/').Any(part => part is "" or "." or "..") ||
                SeedSha256 is null || SeedSha256.Length != 64 || !SeedSha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("Seeded cache needs a package-relative SeedDirectory and full SeedSha256 tree identity.");
        }
        else if (SeedDirectory is not null || SeedSha256 is not null)
            throw new InvalidDataException("Cache seed fields are only valid with CacheMode=seeded.");
        if (MaximumFiles is < 1 or > 16384 || MaximumBytes is < 1 or > 2147483648L || DeadlineSeconds is < 1 or > 60)
            throw new InvalidDataException("Isolation bounds: 1..16384 files, 1..2147483648 bytes, 1..60 seconds.");
    }
}

public sealed record RunStateFile(string Path, long Bytes, string Sha256);
public sealed record RunStateSnapshot(bool Complete, string TreeSha256, long Bytes,
    IReadOnlyList<RunStateFile> Files, IReadOnlyList<string> Issues);

public sealed class RunStorageReport
{
    public int Schema { get; set; } = 1;
    public string Status { get; set; } = "preparing";
    public string CacheMode { get; set; } = "unmanaged";
    public bool? CacheShaders { get; set; }
    public string DriverCache { get; set; } = "uncontrolled";
    public bool DriverNamespaceVerified { get; set; }
    public bool ComparisonReady { get; set; }
    public bool AllowUncontrolledDriverCache { get; set; }
    public string? ContractSha256 { get; set; }
    public string? CacheDirectory { get; set; }
    public string? PriorCacheDirectory { get; set; }
    public string? DriverCacheDirectory { get; set; }
    public string? ConfigSourcePath { get; set; }
    public string? EffectiveConfigPath { get; set; }
    public string? ConfigSha256 { get; set; }
    public string? EffectiveConfigSha256 { get; set; }
    public string? ConfigAfterSha256 { get; set; }
    public string? SeedSha256 { get; set; }
    public RunStateSnapshot? Before { get; set; }
    public RunStateSnapshot? After { get; set; }
    public RunStateSnapshot? DriverAfter { get; set; }
    public bool TargetStopped { get; set; }
    public List<string> Uncontrolled { get; set; } = ["os-page-cache", "driver-cache"];
    public List<string> Issues { get; set; } = [];
    public IReadOnlyList<RuntimeFileMaterialization> RuntimeSeeds { get; set; } = [];
    public Dictionary<string, string> StoragePaths { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> CacheEnvironment { get; set; } = new(StringComparer.Ordinal);
    public List<string> Limitations { get; set; } = [
        "Cold refers to the application cache, not RAM, OS page cache or a fully reset GPU driver.",
        "The effective file pins authored values and shader caching; omitted build defaults are not introspected.",
        "Directory redirection is not an OS sandbox and does not prove an arbitrary executable obeyed it.",
        "Driver namespaces are requested controls, not proof the loaded driver used them."
    ];
}
