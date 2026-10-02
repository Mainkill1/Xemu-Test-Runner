using System.Text.Json.Serialization;
using XemuTestRunner.Runtime;

namespace XemuTestRunner.Networking;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record XisoCampaignApiRequest(
    string Id,
    string Application,
    string? Suite = null,
    string[]? Categories = null,
    string[]? Tests = null,
    [property: JsonPropertyName("test_ids")] int[]? TestIds = null,
    [property: JsonPropertyName("category_ids")] int[]? CategoryIds = null,
    string? CatalogId = null,
    string? Mode = null,
    XisoSettings? Settings = null,
    string? ReferenceApplication = null);

internal sealed partial class AgentJobStore
{
    public object CreateXisoCampaign(XisoCampaignApiRequest request)
    {
        var suiteId = request.Suite;
        var categories = request.Categories;
        var tests = request.Tests;
        var hasNumericSelectors = (request.TestIds?.Length ?? 0) > 0 ||
                                  (request.CategoryIds?.Length ?? 0) > 0;
        if (hasNumericSelectors)
        {
            // Preserve the established cheap validation order before reading a suite.
            if (!IsId(request.Id) || request.Id.Length > 38)
                throw new InvalidDataException("Campaign ID must be a runner ID of at most 38 characters.");
            if (request.Suite is null || request.CatalogId is null)
                throw new InvalidDataException("Numeric XISO selectors require an explicit suite and catalogId from that suite's catalog response.");
            var suite = ReadXisoSuite(request.Suite);
            if (request.CatalogId != suite.Data.Catalog.Id)
                throw Conflict("xiso_catalog_changed", "Numeric XISO selectors refer to a different catalog.",
                    "Read this suite's current catalog and resolve the stable test identities before creating a campaign.");
            var resolved = suite.Data.Catalog.ResolveSelectors(
                request.Categories, request.Tests, request.TestIds, request.CategoryIds);
            suiteId = suite.Data.Id;
            categories = resolved.Categories;
            tests = resolved.Tests;
        }

        return CreateXisoCampaign(new XisoCampaignRequest(
            request.Id,
            request.Application,
            suiteId,
            categories,
            tests,
            request.Mode,
            request.Settings,
            request.ReferenceApplication));
    }

    public object XisoSelectorCategories(string id)
    {
        var catalog = ReadXisoSuite(id).Data.Catalog;
        var items = Enumerable.Range(0, XisoNumericSelectorExtensions.CategoryCount).Select(categoryIndex =>
        {
            var categoryId = XisoNumericSelectorExtensions.CategoryName(categoryIndex);
            var category = XisoCatalog.Categories.Single(x => x.Id == categoryId);
            var count = catalog.Leaves.Count(x => x.Category == categoryId);
            return new
            {
                category_id = categoryIndex,
                id = categoryId,
                name = category.Name,
                count,
                available = count > 0
            };
        }).ToArray();
        return new
        {
            selectorVersion = 1,
            idBase = 0,
            catalogId = catalog.Id,
            catalogSha256 = catalog.Sha256,
            items
        };
    }

    public object XisoSelectorTests(string id, string? category, string? query, int offset, int limit)
    {
        if (category is not null && !XisoCatalog.Categories.Any(x => x.Id == category))
            throw new InvalidDataException("Unknown XISO category.");
        if (query?.Length > 96)
            throw new InvalidDataException("Test search exceeds 96 characters.");

        var catalog = ReadXisoSuite(id).Data.Catalog;
        var found = catalog.Leaves.Select((leaf, testId) => new { leaf, testId })
            .Where(x => (category is null || x.leaf.Category == category) &&
                (query is null || x.leaf.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                 x.leaf.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .Skip(offset).Take(limit + 1).ToArray();
        var items = found.Take(limit).Select(x => new
        {
            test_id = x.testId,
            id = x.leaf.Id,
            revision = x.leaf.Revision,
            name = x.leaf.Name,
            category = x.leaf.Category,
            route = x.leaf.Route,
            freshProcess = x.leaf.FreshProcess,
            timeoutMs = x.leaf.TimeoutMs
        }).ToArray();
        return new
        {
            selectorVersion = 1,
            idBase = 0,
            catalogId = catalog.Id,
            catalogSha256 = catalog.Sha256,
            items,
            nextOffset = found.Length > limit ? (int?)(offset + limit) : null
        };
    }
}
