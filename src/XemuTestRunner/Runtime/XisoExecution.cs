using System.Text.Json;
using System.Text.Json.Serialization;

namespace XemuTestRunner.Runtime;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record XisoExecution(string Image, long PartitionOffsetBytes, long PartitionLengthBytes,
    string SuiteRevision, string IsoSha256, string CatalogId, string[] Tests, string[] Groups, XisoSettings Settings)
{
    [JsonIgnore] public string PlanId => "sha256:" + XisoData.Hash(new { schema = 1, SuiteRevision, IsoSha256, CatalogId, Tests, Groups, settings = Settings.Resolve() });
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Image) || Image.Contains('\\') || Image.Contains(':') || Image.Split('/').Any(x => x is "" or "." or "..")) throw new InvalidDataException("XISO runtime image must be a private relative path.");
        XisoData.CheckHashId("sha256:" + SuiteRevision); XisoData.CheckHashId("sha256:" + IsoSha256); XisoData.CheckHashId(CatalogId);
        if (Tests is null || Tests.Length is < 1 or > 512 || Tests.Distinct(StringComparer.Ordinal).Count() != Tests.Length || Groups is null || Groups.Length > 64)
            throw new InvalidDataException("Invalid XISO resolved selection.");
        foreach (var id in Tests.Concat(Groups)) XisoData.CheckStableId(id);
        if (PartitionOffsetBytes < 0 || PartitionLengthBytes < 8192 || PartitionOffsetBytes % 512 != 0 || PartitionLengthBytes % 512 != 0 || PartitionOffsetBytes > (1L << 44) - PartitionLengthBytes)
            throw new InvalidDataException("Invalid XISO FATX partition geometry.");
        _ = Settings.Resolve();
    }
    public byte[] ConfigBytes()
    {
        Validate();
        var effective = Settings.Resolve();
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            settings = new {
                disable_autorun = false, enable_autorun_immediately = true, enable_shutdown_on_completion = true,
                reboot_or_shutdown_delay = 0, delay_milliseconds_between_tests = 0, enable_xemu_only_tests = true,
                output_directory_path = "e:/xemu_perf_tests", warmup_iterations = effective.Warmups,
                measurement_iterations_multiplier = effective.Multiplier, gpu_completion_mode = effective.Completion
            },
            resolved_plan = new {
                schema_version = 2, plan_id = PlanId, catalog_id = CatalogId,
                selected_leaf_count = Tests.Length, tests = Tests.Select(id => new { id }).ToArray()
            }
        });
    }
}
