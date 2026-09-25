using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XemuTestRunner.Runtime;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record XisoSettings(
    [property: JsonPropertyName("warmup_iterations")] int? Warmups = null,
    [property: JsonPropertyName("measurement_iterations_multiplier")] int? Multiplier = null,
    [property: JsonPropertyName("gpu_completion_mode")] string? Completion = null)
{
    public XisoSettings Resolve(XisoSettings? inherited = null)
    {
        var value = new XisoSettings(Warmups ?? inherited?.Warmups ?? 0,
            Multiplier ?? inherited?.Multiplier ?? 1, Completion ?? inherited?.Completion ?? "per_iteration");
        if (value.Warmups is < 0 or > 100000 || value.Multiplier is < 1 or > 100000 ||
            value.Completion is not ("enqueue" or "batch_complete" or "per_iteration"))
            throw new InvalidDataException("XISO settings require warmup_iterations 0..100000, measurement_iterations_multiplier 1..100000 and a supported gpu_completion_mode.");
        return value;
    }
}

public sealed record XisoLeaf(string Id, int Revision, string Name, string Category, string Route,
    bool FreshProcess, int TimeoutMs);
public sealed record XisoGroup(string Id, string[] Children);
public sealed record XisoCatalog(string Id, string Sha256, XisoLeaf[] Leaves, XisoGroup[] Groups)
{
    public static readonly (string Id, string Name)[] Categories = [
        ("cpu", "CPU and translation"), ("commands", "Command processing and queries"),
        ("shaders", "Shaders and pipelines"), ("textures", "Textures and formats"),
        ("geometry", "Geometry and draw submission"), ("surfaces", "Surfaces and memory"),
        ("scenarios", "Composite scenarios"), ("other", "Other / newly added suites")
    ];

    public static XisoCatalog Parse(byte[] bytes)
    {
        if (bytes.Length > 1024 * 1024) throw new InvalidDataException("XISO catalog exceeds 1 MiB.");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        XisoData.NoDuplicateKeys(document.RootElement);
        var root = document.RootElement;
        if (root.GetProperty("schema_version").GetInt32() != 1) throw new InvalidDataException("Unsupported XISO catalog schema.");
        var id = root.GetProperty("catalog_id").GetString()!;
        XisoData.CheckHashId(id);
        var leaves = new List<XisoLeaf>();
        var groups = new List<XisoGroup>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var entries = root.GetProperty("tests");
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() is < 1 or > 512)
            throw new InvalidDataException("XISO catalog requires 1..512 records.");
        foreach (var entry in entries.EnumerateArray())
        {
            var stableId = entry.GetProperty("id").GetString()!;
            XisoData.CheckStableId(stableId);
            if (!identities.Add(stableId)) throw new InvalidDataException("Duplicate XISO test ID: " + stableId);
            var kind = entry.GetProperty("kind").GetString();
            if (kind == "group")
            {
                var children = entry.GetProperty("child_ids").EnumerateArray().Select(x => x.GetString()!).ToArray();
                if (children.Length == 0 || children.Distinct(StringComparer.Ordinal).Count() != children.Length)
                    throw new InvalidDataException("Invalid XISO structural group.");
                groups.Add(new(stableId, children));
                continue;
            }
            if (kind != "leaf") throw new InvalidDataException("Unknown catalog record kind.");
            if (!entry.GetProperty("supported_targets").EnumerateArray().Any(x => x.GetString() == "xemu"))
                throw new InvalidDataException("This runner requires an xemu-compatible suite catalog.");
            var suite = entry.GetProperty("suite_id").GetString()!;
            var execution = entry.GetProperty("execution");
            var route = execution.GetProperty("legacy_suite").GetString() + "::" + execution.GetProperty("legacy_test").GetString();
            if (route.Length > 256) throw new InvalidDataException("Execution route is too long.");
            var revision = entry.GetProperty("revision").GetInt32();
            var timeout = entry.GetProperty("timeout_ms").GetInt32();
            if (revision < 1 || timeout is < 1 or > 86400000) throw new InvalidDataException("Invalid XISO revision/deadline.");
            var isolation = entry.GetProperty("isolation").GetString();
            if (isolation is not ("same_process" or "fresh_process")) throw new InvalidDataException("Unsupported XISO isolation rule.");
            // The pilot's source contract requires independent train/capacity/control launches,
            // even though its older generated catalog still says same_process.
            var fresh = isolation == "fresh_process" || suite == "shader_lifecycle";
            leaves.Add(new(stableId, revision, entry.GetProperty("display_name").GetString() ?? stableId,
                Category(suite, stableId), route, fresh, timeout));
        }
        if (root.GetProperty("leaf_count").GetInt32() != leaves.Count || root.GetProperty("group_count").GetInt32() != groups.Count)
            throw new InvalidDataException("Catalog counts do not match the actual records.");
        var leafIds = leaves.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        if (groups.Any(g => g.Children.Any(child => !leafIds.Contains(child)))) throw new InvalidDataException("Group refers to a missing leaf.");
        return new(id, XisoData.Sha(bytes), leaves.ToArray(), groups.ToArray());
    }

    public XisoSelection Select(string[]? categories, string[]? tests, string? requestedMode)
    {
        categories ??= []; tests ??= [];
        if (categories.Length > Categories.Length || tests.Length > 512) throw new InvalidDataException("Too many XISO selectors.");
        var mode = requestedMode ?? (categories.Length + tests.Length == 0 ? "smoke" : "sections");
        if (mode is not ("smoke" or "sections" or "full" or "monolithic")) throw new InvalidDataException("XISO mode must be smoke, sections, full or monolithic.");
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var category in categories)
        {
            if (!Categories.Any(x => x.Id == category)) throw new InvalidDataException("Unknown XISO category: " + category);
            var matches = Leaves.Where(x => x.Category == category).ToArray();
            if (matches.Length == 0) throw new InvalidDataException("No tests in category " + category + " for this pinned suite.");
            selected.UnionWith(matches.Select(x => x.Id));
        }
        foreach (var test in tests)
        {
            if (!Leaves.Any(x => x.Id == test)) throw new InvalidDataException("Unknown or non-leaf XISO test: " + test);
            selected.Add(test);
        }
        if (selected.Count == 0)
        {
            if (mode == "sections") throw new InvalidDataException("Select a category or individual test, or request full/smoke mode.");
            if (mode is "full" or "monolithic") selected.UnionWith(Leaves.Select(x => x.Id));
            else foreach (var category in Categories)
            {
                var candidates = Leaves.Where(x => x.Category == category.Id).ToArray();
                var choice = candidates.FirstOrDefault(x => x.Id.EndsWith("pipeline_uniform_only", StringComparison.Ordinal)) ?? candidates.FirstOrDefault();
                if (choice is not null) selected.Add(choice.Id);
            }
        }
        var explicitlySelected = selected.ToHashSet(StringComparer.Ordinal);
        // These five checkpoints are one lifetime experiment, not five independent tests.
        foreach (var leaf in Leaves.Where(x => selected.Contains(x.Id)).ToArray())
            if (leaf.Id.Contains(".vulkan_memory_pressure.", StringComparison.Ordinal))
                selected.UnionWith(Leaves.Where(x => x.Route == leaf.Route).Select(x => x.Id));
        var chosen = Leaves.Where(x => selected.Contains(x.Id)).ToArray();
        if (mode == "monolithic")
        {
            if (chosen.Any(x => x.FreshProcess)) throw new InvalidDataException("This selection includes fresh-process shader/isolation cases. Use sections/full, or select only same-process tests for a monolithic run.");
            return new(mode, chosen, selected.Except(explicitlySelected).Order(StringComparer.Ordinal).ToArray(), [chosen]);
        }
        var chunks = new List<XisoLeaf[]>();
        var current = new List<XisoLeaf>();
        var routes = 0;
        foreach (var route in chosen.GroupBy(x => x.Route, StringComparer.Ordinal))
        {
            var members = route.ToArray();
            var isolated = members.Any(x => x.FreshProcess);
            if (current.Count > 0 && (isolated || current[0].Category != members[0].Category || routes >= 12))
            { chunks.Add(current.ToArray()); current.Clear(); routes = 0; }
            current.AddRange(members); routes++;
            if (isolated) { chunks.Add(current.ToArray()); current.Clear(); routes = 0; }
        }
        if (current.Count > 0) chunks.Add(current.ToArray());
        return new(mode, chosen, selected.Except(explicitlySelected).Order(StringComparer.Ordinal).ToArray(), chunks.ToArray());
    }

    private static string Category(string suite, string id)
    {
        if (suite.StartsWith("cpu_", StringComparison.Ordinal)) return "cpu";
        if (suite.StartsWith("pfifo", StringComparison.Ordinal) || suite is "busy_pfifo" or "report_query") return "commands";
        if (suite is "shader_lifecycle" or "pipeline_texture_switch" or "uniform_thrash") return "shaders";
        if (suite.StartsWith("texture", StringComparison.Ordinal) || id.StartsWith("game_load.s3tc_sync_factor.", StringComparison.Ordinal)) return "textures";
        if (suite is "high_vertex_count" or "primitive_type" or "tiny_draw" or "vertex_buffer_allocation") return "geometry";
        if (suite.StartsWith("surface", StringComparison.Ordinal)) return "surfaces";
        if (suite == "game_load") return "scenarios";
        return "other";
    }
}

public sealed record XisoSelection(string Mode, XisoLeaf[] Leaves, string[] AddedDependencies, XisoLeaf[][] Chunks);

internal static class XisoData
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static string Hash(object value) => Sha(JsonSerializer.SerializeToUtf8Bytes(value, Json));
    internal static void CheckHashId(string? value)
    {
        if (value is null || value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal) || !value.AsSpan(7).ToString().All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new InvalidDataException("Expected a complete lowercase sha256: identifier.");
    }
    internal static void CheckStableId(string? id)
    {
        if (id is null || id.Length is < 1 or > 96 || !id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-') || id.StartsWith('.') || id.EndsWith('.') || id.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid XISO stable test ID.");
    }
    internal static void NoDuplicateKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property: " + property.Name); NoDuplicateKeys(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) NoDuplicateKeys(item);
    }
}
