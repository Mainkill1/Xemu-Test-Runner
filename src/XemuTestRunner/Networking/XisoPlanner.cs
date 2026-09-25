using System.Text;
using System.Text.Json;
using XemuTestRunner.Runtime;

namespace XemuTestRunner.Networking;

/// <summary>Resolve once. Never rebalance a candidate according to its observed speed.</summary>
internal static class XisoPlanner
{
    public static XisoLeaf[] Validate(XisoRegistration registration)
    {
        var manifest = registration.Manifest ?? throw new InvalidDataException("XISO manifest is required.");
        if (manifest.SchemaVersion != 1 || manifest.SourceCommit is not { Length: 40 } || !manifest.SourceCommit.All(Uri.IsHexDigit) ||
            manifest.Qualification is not ("candidate" or "qualified") || !XisoHash.IsSha(manifest.IsoSha256) ||
            !XisoHash.IsSha(manifest.CatalogSha256) || registration.CatalogJson is null || Encoding.UTF8.GetByteCount(registration.CatalogJson) > 1024 * 1024 ||
            XisoHash.Bytes(Encoding.UTF8.GetBytes(registration.CatalogJson)) != manifest.CatalogSha256)
            throw new InvalidDataException("ISO/catalog/source identity is invalid. Use all files from one runner-suite build artifact.");
        using var document = JsonDocument.Parse(registration.CatalogJson, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.GetProperty("schema_version").GetInt32() != 1 || root.GetProperty("catalog_id").GetString() != manifest.CatalogId)
            throw new InvalidDataException("Manifest and bundled catalog identity differ.");
        var records = root.GetProperty("tests").EnumerateArray().ToArray();
        if (records.Length is < 1 or > 512 || records.Select(x => x.GetProperty("id").GetString()).Distinct(StringComparer.Ordinal).Count() != records.Length)
            throw new InvalidDataException("Catalog IDs must be unique and bounded.");
        var leaves = records.Where(x => x.GetProperty("kind").GetString() == "leaf").ToArray();
        var groups = records.Where(x => x.GetProperty("kind").GetString() == "group").ToArray();
        if (root.GetProperty("leaf_count").GetInt32() != leaves.Length || root.GetProperty("group_count").GetInt32() != groups.Length ||
            records.Length != leaves.Length + groups.Length)
            throw new InvalidDataException("Catalog record counts disagree with its content.");
        var ids = leaves.Select(x => x.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
        if (ids.Any(x => !XisoHash.IsTestId(x)) || manifest.Categories is null || manifest.AtomicGroups is null || manifest.Smoke is null ||
            manifest.Categories.Length is < 1 or > 32 || manifest.Categories.Select(x => x.Id).Distinct().Count() != manifest.Categories.Length)
            throw new InvalidDataException("Invalid category/leaf manifest.");
        var membership = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var category in manifest.Categories)
        {
            if (!DiskAssetCatalog.IsValidId(category.Id) || category.Name.Length is < 1 or > 80 || category.Tests is null || category.Tests.Length == 0)
                throw new InvalidDataException("Invalid XISO category.");
            foreach (var id in category.Tests)
                if (!ids.Contains(id) || !membership.TryAdd(id, category.Id))
                    throw new InvalidDataException("Every leaf must belong to exactly one known category.");
        }
        if (membership.Count != ids.Count || manifest.Smoke.Length == 0 || manifest.Smoke.Any(x => !ids.Contains(x)))
            throw new InvalidDataException("Category coverage/smoke selection does not match the catalog.");
        foreach (var group in manifest.AtomicGroups)
            if (group is null || group.Length < 2 || group.Distinct().Count() != group.Length || group.Any(x => !ids.Contains(x)))
                throw new InvalidDataException("Invalid atomic checkpoint group.");
        return leaves.Select(x =>
        {
            var id = x.GetProperty("id").GetString()!;
            if (!x.GetProperty("supported_targets").EnumerateArray().Any(t => t.GetString() == "xemu"))
                throw new InvalidDataException("The selected runner bundle includes a non-xemu leaf.");
            var execution = x.GetProperty("execution");
            var route = execution.GetProperty("legacy_suite").GetString() + "::" + execution.GetProperty("legacy_test").GetString();
            var isolation = x.GetProperty("isolation").GetString()!;
            if (isolation is not ("same_process" or "fresh_process")) throw new InvalidDataException("Unsupported isolation contract: " + isolation);
            var timeout = x.GetProperty("timeout_ms").GetInt32();
            if (timeout is < 1 or > 86400000) throw new InvalidDataException("Invalid leaf watchdog declaration.");
            return new XisoLeaf(id, x.GetProperty("display_name").GetString()!, membership[id], route, timeout, isolation);
        }).ToArray();
    }

    public static XisoCampaignPlan Resolve(XisoSuite suite, XisoCampaignRequest request)
    {
        if (request.Mode is not ("focused" or "qualification" or "full") || request.Repetitions is < 1 or > 10)
            throw new InvalidDataException("mode must be focused, qualification or full; repetitions must be 1..10.");
        var leaves = Validate(suite.Registration);
        var manifest = suite.Registration.Manifest;
        var byId = leaves.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var category in request.Categories ?? [])
        {
            if (category == "all") { selected.UnionWith(byId.Keys); continue; }
            var matched = manifest.Categories.SingleOrDefault(x => x.Id == category)
                ?? throw new InvalidDataException("Unknown XISO category: " + category + ". List the suite's categories first.");
            selected.UnionWith(matched.Tests);
        }
        foreach (var id in request.Tests ?? [])
        {
            if (!byId.ContainsKey(id)) throw new InvalidDataException("Unknown XISO leaf: " + id);
            selected.Add(id);
        }
        if (selected.Count == 0) selected.UnionWith(request.Mode == "focused" ? manifest.Smoke : byId.Keys);
        var initial = selected.ToHashSet(StringComparer.Ordinal);
        bool changed;
        do
        {
            var count = selected.Count;
            foreach (var group in manifest.AtomicGroups)
                if (group.Any(selected.Contains)) selected.UnionWith(group);
            changed = count != selected.Count;
        } while (changed);
        var ordered = leaves.Where(x => selected.Contains(x.Id)).ToArray();
        // Same execution route shares initialization/lifetime. Preserve it even
        // when several stable output leaves are exposed by that route.
        var units = ordered.GroupBy(x => x.Route, StringComparer.Ordinal).Select(g => g.ToArray()).ToArray();
        var partitions = new List<XisoLeaf[]>();
        if (request.Mode == "full") partitions.Add(ordered);
        else
        {
            var current = new List<XisoLeaf>();
            foreach (var unit in units)
            {
                var isolate = unit.Any(x => x.Isolation == "fresh_process");
                if (current.Count > 0 && (isolate || current[0].Category != unit[0].Category ||
                    request.Mode == "qualification" && current.Select(x => x.Route).Distinct().Count() >= 24))
                { partitions.Add(current.ToArray()); current.Clear(); }
                current.AddRange(unit);
                if (isolate) { partitions.Add(current.ToArray()); current.Clear(); }
            }
            if (current.Count > 0) partitions.Add(current.ToArray());
            // This final pass detects cross-test state/order effects omitted by
            // process-isolated shards. It is explicitly part of qualification.
            if (request.Mode == "qualification" && partitions.Count > 1) partitions.Add(ordered);
        }
        var settings = (request.Settings ?? new XisoSettings()).Resolve(suite.Registration.Defaults).GuestValues();
        using var catalog = JsonDocument.Parse(suite.Registration.CatalogJson);
        var groups = catalog.RootElement.GetProperty("tests").EnumerateArray().Where(x => x.GetProperty("kind").GetString() == "group").ToArray();
        var chunks = partitions.Select((part, index) =>
        {
            var ids = part.Select(x => x.Id).ToArray();
            var tests = ids.Select(id => new { id }).ToArray();
            var sha = XisoHash.Json(new { schemaVersion = 1, catalogId = manifest.CatalogId, settings, tests });
            var config = JsonSerializer.Serialize(new { settings, resolved_plan = new { schema_version = 2,
                plan_id = "sha256:" + sha, catalog_id = manifest.CatalogId, selected_leaf_count = ids.Length, tests } });
            var groupIds = groups.Where(g => g.GetProperty("child_ids").EnumerateArray().Any(x => ids.Contains(x.GetString(), StringComparer.Ordinal)))
                .Select(g => g.GetProperty("id").GetString()!).ToArray();
            return new XisoChunk("c" + (index + 1).ToString("D3"), ids, groupIds, sha, config);
        }).ToArray();
        if (chunks.Length * request.Repetitions * (request.Reference is null ? 1 : 4) > 512)
            throw new InvalidDataException("Campaign exceeds 512 child attempts; select a smaller category set.");
        return new(suite.Id, suite.Revision, manifest.IsoSha256, manifest.CatalogId, "execution-units-v1",
            settings, ordered.Select(x => x.Id).ToArray(), selected.Except(initial).Order(StringComparer.Ordinal).ToArray(), chunks);
    }
}
