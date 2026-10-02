namespace XemuTestRunner.Runtime;

public sealed record XisoResolvedSelectors(string[] Tests, string[] Categories);

public static class XisoNumericSelectorExtensions
{
    // These IDs are part of selectorVersion 1. Never reorder or repurpose them;
    // append a new mapping and bump the selector version for incompatible changes.
    public const int CategoryCount = 8;

    public static string CategoryName(int id) => id switch
    {
        0 => "cpu",
        1 => "commands",
        2 => "shaders",
        3 => "textures",
        4 => "geometry",
        5 => "surfaces",
        6 => "scenarios",
        7 => "other",
        _ => throw new InvalidDataException("Unknown XISO category ID: " + id +
            ". Read the pinned suite category catalog before selecting numeric categories.")
    };

    public static XisoResolvedSelectors ResolveSelectors(this XisoCatalog catalog,
        string[]? categories, string[]? tests, int[]? testIds, int[]? categoryIds)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        testIds ??= [];
        categoryIds ??= [];
        if (testIds.Length > 512)
            throw new InvalidDataException("Too many numeric XISO test selectors.");
        if (categoryIds.Length > CategoryCount)
            throw new InvalidDataException("Too many numeric XISO category selectors.");

        var resolvedTests = new List<string>(tests ?? []);
        foreach (var id in testIds.Distinct().Order())
        {
            if (id < 0 || id >= catalog.Leaves.Length)
                throw new InvalidDataException("Unknown XISO test ID: " + id +
                    ". Read the pinned suite test catalog before selecting numeric IDs.");
            resolvedTests.Add(catalog.Leaves[id].Id);
        }

        var resolvedCategories = new List<string>(categories ?? []);
        foreach (var id in categoryIds.Distinct().Order())
        {
            var category = CategoryName(id);
            if (!catalog.Leaves.Any(x => x.Category == category))
                throw new InvalidDataException("No tests in category " + id + " (" + category +
                    ") for this pinned suite.");
            resolvedCategories.Add(category);
        }

        return new(
            resolvedTests.Distinct(StringComparer.Ordinal).ToArray(),
            resolvedCategories.Distinct(StringComparer.Ordinal).ToArray());
    }
}
