namespace XemuTestRunner.Runtime;

public sealed record XisoResolvedSelectors(string[] Tests, string[] Categories);

public static class XisoNumericSelectorExtensions
{
    // These IDs are part of selectorVersion 1. Never reorder or repurpose them;
    // append a new mapping and bump the selector version for incompatible changes.
    public const int TestGroupCount = 8;

    public static string TestGroupCategory(int id) => id switch
    {
        0 => "cpu",
        1 => "commands",
        2 => "shaders",
        3 => "textures",
        4 => "geometry",
        5 => "surfaces",
        6 => "scenarios",
        7 => "other",
        _ => throw new InvalidDataException("Unknown XISO test-group ID: " + id +
            ". Read the pinned suite category catalog before selecting numeric groups.")
    };

    public static XisoResolvedSelectors ResolveSelectors(this XisoCatalog catalog,
        string[]? categories, string[]? tests, int[]? testIds, int[]? testGroups)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        testIds ??= [];
        testGroups ??= [];
        if (testIds.Length > 512)
            throw new InvalidDataException("Too many numeric XISO test selectors.");
        if (testGroups.Length > TestGroupCount)
            throw new InvalidDataException("Too many numeric XISO test-group selectors.");

        var resolvedTests = new List<string>(tests ?? []);
        foreach (var id in testIds.Distinct().Order())
        {
            if (id < 0 || id >= catalog.Leaves.Length)
                throw new InvalidDataException("Unknown XISO test ID: " + id +
                    ". Read the pinned suite test catalog before selecting numeric IDs.");
            resolvedTests.Add(catalog.Leaves[id].Id);
        }

        var resolvedCategories = new List<string>(categories ?? []);
        foreach (var id in testGroups.Distinct().Order())
        {
            var category = TestGroupCategory(id);
            if (!catalog.Leaves.Any(x => x.Category == category))
                throw new InvalidDataException("No tests in test group " + id + " (" + category +
                    ") for this pinned suite.");
            resolvedCategories.Add(category);
        }

        return new(
            resolvedTests.Distinct(StringComparer.Ordinal).ToArray(),
            resolvedCategories.Distinct(StringComparer.Ordinal).ToArray());
    }
}
