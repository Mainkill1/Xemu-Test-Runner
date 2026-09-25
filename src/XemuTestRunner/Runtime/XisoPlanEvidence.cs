using System.Text;
using System.Text.Json;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Runtime;

internal static class XisoPlanEvidence
{
    public static byte[] ReferenceForPlan(XisoPlanDefinition plan, byte[] raw, byte[] reference,
        FatxResultReader reader, string results, string runtimeDirectory)
    {
        plan.Validate();
        var config = reader.ReadFile(XisoDiskPreparer.ConfigPath, 1024 * 1024)
            ?? throw new InvalidDataException("Guest configuration is missing from the stopped runtime disk.");
        var receipt = reader.ReadFile(XisoDiskPreparer.ReceiptPath, 1024 * 1024)
            ?? throw new InvalidDataException("Guest resolved-plan receipt is missing; completion cannot be inferred from process exit.");
        Preserve(Path.Combine(results, "guest", "config.json"), config);
        Preserve(Path.Combine(results, "guest", "resolved-plan-result.json"), receipt);
        var preparation = Path.Combine(runtimeDirectory, "xiso-preparation.json");
        RunStateInventory.NoLinks(preparation);
        if (!File.Exists(preparation) || new FileInfo(preparation).Length > 65536)
            throw new InvalidDataException("Verified preboot preparation receipt is missing or oversized.");
        Preserve(Path.Combine(results, "guest", "preparation.json"), File.ReadAllBytes(preparation));
        if (XisoHash.Bytes(config) != plan.ConfigSha256)
            throw new InvalidDataException("The guest config differs from the runner's injected bytes.");
        using var configDocument = JsonDocument.Parse(config);
        var root = configDocument.RootElement;
        var tests = plan.Leaves.Select(id => new { id }).ToArray();
        var canonical = XisoHash.Json(new { schemaVersion = 1, catalogId = plan.CatalogId,
            settings = root.GetProperty("settings"), tests });
        if (canonical != plan.PlanSha256) throw new InvalidDataException("Resolved plan digest does not match its settings/ordered leaf selection.");
        using var receiptDocument = JsonDocument.Parse(receipt);
        var result = receiptDocument.RootElement;
        if (result.GetProperty("schema_version").GetInt32() != 2 || result.GetProperty("catalog_id").GetString() != plan.CatalogId ||
            result.GetProperty("plan_id").GetString() != "sha256:" + plan.PlanSha256 || result.GetProperty("completion").GetString() != "COMPLETE" ||
            result.GetProperty("selected_leaf_count").GetInt32() != plan.Leaves.Length || result.GetProperty("emitted_leaf_count").GetInt32() != plan.Leaves.Length)
            throw new InvalidDataException("Guest receipt does not confirm this exact catalog/plan/leaf set.");
        using var actual = JsonDocument.Parse(raw);
        using var expected = JsonDocument.Parse(reference);
        if (actual.RootElement.ValueKind != JsonValueKind.Array || expected.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Guest result/reference must contain complete result arrays.");
        var required = plan.Leaves.Concat(plan.Groups).ToHashSet(StringComparer.Ordinal);
        var actualIds = actual.RootElement.EnumerateArray().Select(x => x.GetProperty("id").GetString()!).ToArray();
        if (actualIds.Length != required.Count || !required.SetEquals(actualIds))
            throw new InvalidDataException("Guest result contains missing, extra or duplicate records for this chunk.");
        var chosen = expected.RootElement.EnumerateArray().Where(x => required.Contains(x.GetProperty("id").GetString()!)).ToArray();
        if (chosen.Length != required.Count || !required.SetEquals(chosen.Select(x => x.GetProperty("id").GetString()!)))
            throw new InvalidDataException("Pinned reference lacks this chunk's exact oracle records. Do not infer a new oracle from the candidate.");
        AtomicJson.Write(Path.Combine(results, "guest", "coverage.json"), new
        {
            schemaVersion = 1, planSha256 = plan.PlanSha256, catalogId = plan.CatalogId,
            selected = plan.Leaves.Length, emitted = plan.Leaves.Length,
            groups = plan.Groups.Length, missing = 0, extra = 0, duplicate = 0, complete = true,
            configSha256 = plan.ConfigSha256, receiptSha256 = XisoHash.Bytes(receipt)
        });
        // Subset the retained reference in memory only. Original raw evidence and
        // oracle file are never modified or promoted from a candidate result.
        return JsonSerializer.SerializeToUtf8Bytes(chosen);
    }
    private static void Preserve(string path, byte[] bytes)
    {
        RunStateInventory.NoLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length != bytes.Length || XisoHash.Bytes(File.ReadAllBytes(path)) != XisoHash.Bytes(bytes))
                throw new InvalidDataException("Existing guest evidence conflicts with this attempt.");
            return;
        }
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(bytes); output.Flush(true);
    }
}
