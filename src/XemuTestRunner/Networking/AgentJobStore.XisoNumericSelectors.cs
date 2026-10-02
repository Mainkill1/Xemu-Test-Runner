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
    [property: JsonPropertyName("test_groups")] int[]? TestGroups = null,
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
                                  (request.TestGroups?.Length ?? 0) > 0;
        if (hasNumericSelectors)
        {
            var suite = ReadXisoSuite(request.Suite);
            var resolved = suite.Data.Catalog.ResolveNumericSelectors(request.TestIds, request.TestGroups);
            suiteId = suite.Data.Id;
            categories = MergeSelectors(request.Categories, resolved.Categories);
            tests = MergeSelectors(request.Tests, resolved.Tests);
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
        var items = XisoCatalog.Categories.Select((category, groupId) =>
        {
            var count = catalog.Leaves.Count(x => x.Category == category.Id);
            return new
            {
                group_id = groupId,
                id = category.Id,
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

    private static string[] MergeSelectors(string[]? named, string[] numeric) =>
        (named ?? []).Concat(numeric).Distinct(StringComparer.Ordinal).ToArray();
}
