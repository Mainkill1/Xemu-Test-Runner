using System.Runtime.CompilerServices;
using System.Text.Json;
using XemuTestRunner.Runtime;

internal static class NumericSelectorChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        var leaves = new[]
        {
            new XisoLeaf("cpu.zero", 1, "CPU zero", "cpu", "Fixture::cpu.zero", false, 30_000),
            new XisoLeaf("commands.one", 1, "Commands one", "commands", "Fixture::commands.one", false, 30_000),
            new XisoLeaf("shader.two", 1, "Shader two", "shaders", "Fixture::shader.two", false, 30_000),
            new XisoLeaf("shader.three", 1, "Shader three", "shaders", "Fixture::shader.three", true, 30_000),
            new XisoLeaf("texture.four", 1, "Texture four", "textures", "Fixture::texture.four", false, 30_000)
        };
        var catalog = new XisoCatalog("sha256:" + new string('a', 64), new string('b', 64), leaves, []);

        Require(Enumerable.Range(0, XisoNumericSelectorExtensions.CategoryCount)
                .Select(XisoNumericSelectorExtensions.CategoryName)
                .SequenceEqual(["cpu", "commands", "shaders", "textures", "geometry", "surfaces", "scenarios", "other"], StringComparer.Ordinal),
            "Selector-version 1 category IDs changed.");

        var individual = Select(catalog, null, null, [3, 0, 3], null);
        Require(individual.Mode == "sections", "Numeric selectors did not choose sections mode.");
        Require(Ids(individual).SequenceEqual(["cpu.zero", "shader.three"], StringComparer.Ordinal),
            "Numeric test IDs did not resolve in pinned catalog order or de-duplicate.");

        var grouped = Select(catalog, null, null, null, [2, 0, 2]);
        Require(Ids(grouped).SequenceEqual(["cpu.zero", "shader.two", "shader.three"], StringComparer.Ordinal),
            "Numeric categorys did not resolve to the stable subsystem table.");

        var union = Select(catalog, ["cpu"], ["texture.four"], [1], [2]);
        Require(Ids(union).SequenceEqual(leaves.Select(x => x.Id), StringComparer.Ordinal),
            "Named and numeric selectors were not unioned.");

        Reject(() => Select(catalog, null, null, [-1], null), "Negative test ID was accepted.");
        Reject(() => Select(catalog, null, null, [leaves.Length], null), "Out-of-range test ID was accepted.");
        Reject(() => Select(catalog, null, null, null, [-1]), "Negative category ID was accepted.");
        Reject(() => Select(catalog, null, null, null, [XisoNumericSelectorExtensions.CategoryCount]),
            "Out-of-range category ID was accepted.");
        Reject(() => Select(catalog, null, null, null, [5]),
            "An unavailable category was accepted for this suite.");

        VerifyRequestBinding();
    }

    private static XisoSelection Select(XisoCatalog catalog, string[]? categories, string[]? tests,
        int[]? testIds, int[]? categoryIds)
    {
        var resolved = catalog.ResolveSelectors(categories, tests, testIds, categoryIds);
        return catalog.Select(resolved.Categories, resolved.Tests, null);
    }

    private static void VerifyRequestBinding()
    {
        var requestType = typeof(XisoCatalog).Assembly.GetType(
            "XemuTestRunner.Networking.XisoCampaignApiRequest", throwOnError: true)!;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var request = JsonSerializer.Deserialize(
            """{"id":"focused","application":"candidate","suite":"pilot","catalogId":"sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","test_ids":[0,3,29],"category_ids":[0,2]}""",
            requestType, options) ?? throw new InvalidOperationException("Numeric XISO request did not deserialize.");
        var testIds = (int[]?)requestType.GetProperty("TestIds")?.GetValue(request);
        var categoryIds = (int[]?)requestType.GetProperty("CategoryIds")?.GetValue(request);
        var catalogId = (string?)requestType.GetProperty("CatalogId")?.GetValue(request);
        Require(testIds is not null && testIds.SequenceEqual([0, 3, 29]),
            "Server request binding lost test_ids.");
        Require(categoryIds is not null && categoryIds.SequenceEqual([0, 2]),
            "Server request binding lost category_ids.");
        Require(catalogId == "sha256:" + new string('b', 64), "Server request binding lost numeric catalog identity.");

        RejectJson(() => JsonSerializer.Deserialize(
            """{"id":"focused","application":"candidate","testIds":[0]}""",
            requestType, options), "Unexpected numeric selector spelling was accepted.");
    }

    private static IEnumerable<string> Ids(XisoSelection selection) => selection.Leaves.Select(x => x.Id);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void RejectJson(Action action, string message)
    {
        try { action(); }
        catch (JsonException) { return; }
        throw new InvalidOperationException(message);
    }
}
