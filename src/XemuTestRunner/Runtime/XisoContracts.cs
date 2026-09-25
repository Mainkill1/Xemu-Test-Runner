using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XemuTestRunner.Runtime;

/// <summary>A closed, small set of optional guest overrides. Null means inherit the saved suite default.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class XisoSettings
{
    public int? WarmupIterations { get; set; }
    public int? MeasurementMultiplier { get; set; }
    public string? GpuCompletionMode { get; set; }
    public XisoSettings Resolve(XisoSettings? defaults = null)
    {
        var value = new XisoSettings
        {
            WarmupIterations = WarmupIterations ?? defaults?.WarmupIterations ?? 0,
            MeasurementMultiplier = MeasurementMultiplier ?? defaults?.MeasurementMultiplier ?? 1,
            GpuCompletionMode = GpuCompletionMode ?? defaults?.GpuCompletionMode ?? "per_iteration"
        };
        if (value.WarmupIterations is < 0 or > 100000 || value.MeasurementMultiplier is < 1 or > 100000 ||
            value.GpuCompletionMode is not ("enqueue" or "batch_complete" or "per_iteration"))
            throw new InvalidDataException("XISO settings: warmupIterations 0..100000, measurementMultiplier 1..100000, gpuCompletionMode enqueue/batch_complete/per_iteration.");
        return value;
    }
    public object GuestValues() => new
    {
        disable_autorun = false, enable_autorun_immediately = true,
        enable_shutdown_on_completion = true, reboot_or_shutdown_delay = 0,
        enable_xemu_only_tests = true, output_directory_path = "e:/xemu_perf_tests",
        warmup_iterations = WarmupIterations, measurement_iterations_multiplier = MeasurementMultiplier,
        gpu_completion_mode = GpuCompletionMode
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class XisoPlanDefinition : IJsonOnDeserialized
{
    public string Image { get; set; } = "";
    public long PartitionOffsetBytes { get; set; }
    public long PartitionLengthBytes { get; set; }
    public string CatalogId { get; set; } = "";
    public string PlanSha256 { get; set; } = "";
    public string ConfigJson { get; set; } = "";
    public string ConfigSha256 { get; set; } = "";
    public string[] Leaves { get; set; } = [];
    public string[] Groups { get; set; } = [];
    void IJsonOnDeserialized.OnDeserialized() => Validate();
    public void Validate()
    {
        if (Image.Length is < 1 or > 256 || Image.Contains('\\') || Image.Contains(':') ||
            Image.Split('/').Any(x => x is "" or "." or ".."))
            throw new InvalidDataException("XISO image must name a private runtime disk.");
        if (PartitionOffsetBytes < 0 || PartitionOffsetBytes % 512 != 0 || PartitionLengthBytes < 8192 ||
            PartitionLengthBytes % 512 != 0 || PartitionOffsetBytes > (1L << 44) - PartitionLengthBytes)
            throw new InvalidDataException("Invalid XISO FATX partition geometry.");
        if (!XisoHash.IsSha(PlanSha256) || !XisoHash.IsSha(ConfigSha256) || !CatalogId.StartsWith("sha256:", StringComparison.Ordinal) ||
            !XisoHash.IsSha(CatalogId[7..]) || Encoding.UTF8.GetByteCount(ConfigJson) > 1024 * 1024 ||
            XisoHash.Bytes(Encoding.UTF8.GetBytes(ConfigJson)) != ConfigSha256 || Leaves is null || Groups is null ||
            Leaves.Length is < 1 or > 512 || Leaves.Concat(Groups).Any(x => !XisoHash.IsTestId(x)) ||
            Leaves.Concat(Groups).Distinct(StringComparer.Ordinal).Count() != Leaves.Length + Groups.Length)
            throw new InvalidDataException("Invalid XISO plan identity, selection or config digest.");
        using var document = JsonDocument.Parse(ConfigJson);
        var plan = document.RootElement.GetProperty("resolved_plan");
        if (plan.GetProperty("schema_version").GetInt32() != 2 || plan.GetProperty("plan_id").GetString() != "sha256:" + PlanSha256 ||
            plan.GetProperty("catalog_id").GetString() != CatalogId || plan.GetProperty("selected_leaf_count").GetInt32() != Leaves.Length ||
            !plan.GetProperty("tests").EnumerateArray().Select(x => x.GetProperty("id").GetString()).SequenceEqual(Leaves))
            throw new InvalidDataException("XISO config does not describe its pinned plan.");
    }
}

internal static class XisoHash
{
    public static bool IsSha(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static bool IsTestId(string? value) => value is { Length: >= 1 and <= 96 } &&
        value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.') && !value.Contains("..", StringComparison.Ordinal);
    public static string Bytes(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    public static string Json(object value) => Bytes(JsonSerializer.SerializeToUtf8Bytes(value));
}
