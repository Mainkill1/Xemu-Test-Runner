using System.Text.Json;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Runtime;

internal static class XisoGuestEvidence
{
    public static void PreservePreparation(string runtime, string results)
    {
        foreach (var name in new[] { "xiso-guest-config.json", "xiso-preparation.json" })
        {
            var path = RuntimeStateManager.ResolveInside(runtime, name); RunStateInventory.NoLinks(path);
            if (!File.Exists(path)) throw new InvalidDataException("XISO preparation receipt missing: " + name);
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("XISO preparation evidence exceeds its bound.");
            Preserve(Path.Combine(results, "guest", name), File.ReadAllBytes(path));
        }
    }
    public static GuestEvaluation Evaluate(XisoExecution plan, FatxResultReader reader, byte[] raw,
        byte[] reference, string results, CancellationToken ct)
    {
        var checks = new List<AssessmentCheck>();
        var receipt = reader.ReadFile("xemu_perf_tests/resolved-plan-result.json", 65536)
            ?? throw new InvalidDataException("xiso_receipt_missing: the guest did not close its selected plan.");
        Preserve(Path.Combine(results, "guest", "resolved-plan-result.json"), receipt);
        using var receiptDocument = JsonDocument.Parse(receipt);
        var root = receiptDocument.RootElement; XisoData.NoDuplicateKeys(root);
        var schema = root.GetProperty("schema_version").GetInt32();
        var receiptMatches = schema is 1 or 2 && root.GetProperty("plan_id").GetString() == plan.PlanId &&
            root.GetProperty("selected_leaf_count").GetInt32() == plan.Tests.Length &&
            root.GetProperty("emitted_leaf_count").GetInt32() == plan.Tests.Length &&
            root.GetProperty("completion").GetString() == "COMPLETE";
        if (schema == 2) receiptMatches &= root.GetProperty("catalog_id").GetString() == plan.CatalogId;
        checks.Add(new("xiso_plan_receipt", receiptMatches, "evidence", receiptMatches ?
            "Guest completion receipt matches the injected selection." : "Guest receipt/catalog/count differs from the frozen plan."));
        using var actualDocument = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 32 });
        using var expectedDocument = JsonDocument.Parse(reference, new JsonDocumentOptions { MaxDepth = 32 });
        XisoData.NoDuplicateKeys(actualDocument.RootElement); XisoData.NoDuplicateKeys(expectedDocument.RootElement);
        var actual = Records(actualDocument.RootElement);
        var expected = Records(expectedDocument.RootElement);
        var required = plan.Tests.Concat(plan.Groups).ToHashSet(StringComparer.Ordinal);
        var complete = required.SetEquals(actual.Keys);
        checks.Add(new("xiso_exact_coverage", complete, "evidence", $"Selected {plan.Tests.Length} leaves and {plan.Groups.Length} groups; received {actual.Count}. Missing {required.Except(actual.Keys).Count()}, extra {actual.Keys.Except(required).Count()}."));
        foreach (var id in plan.Groups)
        {
            var valid = actual.TryGetValue(id, out var group) && group.GetProperty("kind").GetString() == "group" &&
                (!group.TryGetProperty("outcome", out var outcome) || outcome.GetString() == "PASS");
            checks.Add(new("xiso_group:" + id, valid, "evidence", "Structural group outcome; never treated as an independent timing sample."));
        }
        var missingOracle = plan.Tests.Where(id => !expected.ContainsKey(id)).ToArray();
        if (missingOracle.Length > 0)
        {
            checks.Add(new("xiso_oracle_coverage", false, "correctness", $"No pinned oracle for {missingOracle.Length} selected leaves. Actual results remain evidence, not a self-approved baseline."));
            return new(checks, [], []);
        }
        // A reference may cover the full suite; compare only these selected leaves.
        // Composite parent counters depend on selection and are validated above.
        var expectedBytes = JsonSerializer.SerializeToUtf8Bytes(plan.Tests.Select(id => expected[id]).ToArray());
        var actualBytes = JsonSerializer.SerializeToUtf8Bytes(plan.Tests.Where(actual.ContainsKey).Select(id => actual[id]).ToArray());
        var parsed = GuestResultParser.Evaluate(actualBytes, expectedBytes, ct);
        checks.AddRange(parsed.Checks);
        var qualified = complete && receiptMatches && checks.All(x => x.Passed);
        AtomicJson.Write(Path.Combine(results, "guest", "xiso-coverage.json"), new {
            schemaVersion = 1, planId = plan.PlanId, plan.SuiteRevision, plan.IsoSha256, plan.CatalogId,
            selected = plan.Tests.Length, missing = required.Except(actual.Keys).ToArray(), extra = actual.Keys.Except(required).ToArray(), complete, receiptMatches });
        return new(checks, qualified ? parsed.Measurements : [], parsed.Records);
    }
    private static Dictionary<string, JsonElement> Records(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > 512) throw new InvalidDataException("XISO results require a complete bounded record array.");
        var records = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var record in root.EnumerateArray())
        {
            var id = record.GetProperty("id").GetString()!; XisoData.CheckStableId(id);
            if (!records.TryAdd(id, record)) throw new InvalidDataException("Duplicate XISO result ID: " + id);
        }
        return records;
    }
    private static void Preserve(string path, byte[] bytes)
    {
        RunStateInventory.NoLinks(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            if (XisoData.Sha(File.ReadAllBytes(path)) != XisoData.Sha(bytes)) throw new InvalidDataException("XISO evidence changed after publication.");
            return;
        }
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); file.Write(bytes); file.Flush(true);
    }
}
