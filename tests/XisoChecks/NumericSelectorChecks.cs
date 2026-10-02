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

        var individual = catalog.Select(null, null, [3, 0, 3], null, null);
        Require(individual.Mode == "sections", "Numeric selectors did not choose sections mode.");
        Require(Ids(individual).SequenceEqual(["cpu.zero", "shader.three"], StringComparer.Ordinal),
            "Numeric test IDs did not resolve in pinned catalog order or de-duplicate.");

        var grouped = catalog.Select(null, null, null, [2, 0, 2], null);
        Require(Ids(grouped).SequenceEqual(["cpu.zero", "shader.two", "shader.three"], StringComparer.Ordinal),
            "Numeric test groups did not resolve to the stable subsystem table.");

        var union = catalog.Select(["cpu"], ["texture.four"], [1], [2], null);
        Require(Ids(union).SequenceEqual(leaves.Select(x => x.Id), StringComparer.Ordinal),
            "Named and numeric selectors were not unioned.");

        Reject(() => catalog.Select(null, null, [-1], null, null), "Negative test ID was accepted.");
        Reject(() => catalog.Select(null, null, [leaves.Length], null, null), "Out-of-range test ID was accepted.");
        Reject(() => catalog.Select(null, null, null, [-1], null), "Negative test-group ID was accepted.");
        Reject(() => catalog.Select(null, null, null, [XisoCatalog.Categories.Length], null),
            "Out-of-range test-group ID was accepted.");

        VerifyRequestBinding();
    }

    private static void VerifyRequestBinding()
    {
        var requestType = typeof(XisoCatalog).Assembly.GetType(
            "XemuTestRunner.Networking.XisoCampaignApiRequest", throwOnError: true)!;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var request = JsonSerializer.Deserialize(
            """{"id":"focused","application":"candidate","suite":"pilot","test_ids":[0,3,29],"test_groups":[0,2]}""",
            requestType, options) ?? throw new InvalidOperationException("Numeric XISO request did not deserialize.");
        var testIds = (int[]?)requestType.GetProperty("TestIds")?.GetValue(request);
        var testGroups = (int[]?)requestType.GetProperty("TestGroups")?.GetValue(request);
        Require(testIds is not null && testIds.SequenceEqual([0, 3, 29]),
            "Server request binding lost test_ids.");
        Require(testGroups is not null && testGroups.SequenceEqual([0, 2]),
            "Server request binding lost test_groups.");

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
