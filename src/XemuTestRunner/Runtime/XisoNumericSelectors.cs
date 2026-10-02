namespace XemuTestRunner.Runtime;

public sealed record XisoResolvedNumericSelectors(string[] Tests, string[] Categories);

public static class XisoNumericSelectorExtensions
{
    public static XisoResolvedNumericSelectors ResolveNumericSelectors(
        this XisoCatalog catalog, int[]? testIds, int[]? testGroups)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        testIds ??= [];
        testGroups ??= [];
        if (testIds.Length > 512)
            throw new InvalidDataException("Too many numeric XISO test selectors.");
        if (testGroups.Length > XisoCatalog.Categories.Length)
            throw new InvalidDataException("Too many numeric XISO test-group selectors.");

        var tests = new List<string>();
        foreach (var id in testIds.Distinct().Order())
        {
            if (id < 0 || id >= catalog.Leaves.Length)
                throw new InvalidDataException("Unknown XISO test ID: " + id + ". Read the pinned suite test catalog before selecting numeric IDs.");
            tests.Add(catalog.Leaves[id].Id);
        }

        var categories = new List<string>();
        foreach (var id in testGroups.Distinct().Order())
        {
            if (id < 0 || id >= XisoCatalog.Categories.Length)
                throw new InvalidDataException("Unknown XISO test-group ID: " + id + ". Read the pinned suite category catalog before selecting numeric groups.");
            var category = XisoCatalog.Categories[id].Id;
            if (!catalog.Leaves.Any(x => x.Category == category))
                throw new InvalidDataException("No tests in test group " + id + " (" + category + ") for this pinned suite.");
            categories.Add(category);
        }

        return new(tests.ToArray(), categories.ToArray());
    }

    public static XisoSelection Select(this XisoCatalog catalog,
        string[]? categories, string[]? tests, int[]? testIds, int[]? testGroups,
        string? requestedMode)
    {
        var numeric = catalog.ResolveNumericSelectors(testIds, testGroups);
        var combinedCategories = (categories ?? []).Concat(numeric.Categories)
            .Distinct(StringComparer.Ordinal).ToArray();
        var combinedTests = (tests ?? []).Concat(numeric.Tests)
            .Distinct(StringComparer.Ordinal).ToArray();
        return catalog.Select(combinedCategories, combinedTests, requestedMode);
    }
}
