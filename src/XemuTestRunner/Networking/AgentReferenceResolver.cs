namespace XemuTestRunner.Networking;

/// <summary>Human handles only: every successful resolution returns the canonical full digest.</summary>
internal static class AgentReferenceResolver
{
    public const int MaximumEntries = 10000;

    public static string Resolve(string? reference, IReadOnlyList<string> candidates)
    {
        if (reference is null || reference.Length is < 12 or > 64 || !reference.All(Uri.IsHexDigit))
            throw new AgentRequestException(400, "reference_invalid", "A reference must contain 12..64 hexadecimal characters.",
                "Copy an unambiguous reference or the complete SHA-256 from the relevant catalog.");
        reference = reference.ToLowerInvariant();
        var matches = candidates.Where(value => value.StartsWith(reference, StringComparison.Ordinal)).Take(2).ToArray();
        if (matches.Length == 0)
            throw new AgentRequestException(404, "reference_not_found", "That reference is not present in this catalog.",
                "Refresh the catalog and select an existing item. Nothing was selected or changed.");
        if (matches.Length != 1)
            throw new AgentRequestException(409, "reference_ambiguous", "More than one item has that reference prefix.",
                "Use a longer reference or the complete SHA-256. Nothing was selected or changed.");
        return matches[0];
    }

    public static string[] Catalog(string root, bool directories)
    {
        BuildResultStore.CheckPath(root);
        if (!Directory.Exists(root)) return [];
        var values = new List<string>();
        var inspected = 0;
        var paths = directories ? Directory.EnumerateDirectories(root) : Directory.EnumerateFiles(root, "*.json");
        foreach (var path in paths)
        {
            // Test revisions have both full and summary metadata files.
            if (++inspected > MaximumEntries * 2 + 16)
                throw new AgentRequestException(413, "catalog_limit", "The reference catalog exceeds the safe enumeration limit.",
                    "Inspect catalog retention before resolving references; no partial catalog is used for selection.");
            var value = directories ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
            if (value.Length != 64 || !value.All(Uri.IsHexDigit)) continue;
            BuildResultStore.CheckPath(path);
            if (value != value.ToLowerInvariant()) throw new InvalidDataException("Stored digest names must be canonical lowercase.");
            values.Add(value);
            if (values.Count > MaximumEntries)
                throw new AgentRequestException(413, "catalog_limit", "The reference catalog exceeds 10000 entries.",
                    "Inspect catalog retention; no incomplete catalog is used to resolve a prefix.");
        }
        return values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    public static string Display(IReadOnlyList<string> sorted, int index)
    {
        var length = 12;
        foreach (var neighbour in new[] { index - 1, index + 1 })
        {
            if (neighbour < 0 || neighbour >= sorted.Count) continue;
            var common = 0;
            while (common < 64 && sorted[index][common] == sorted[neighbour][common]) common++;
            length = Math.Max(length, common + 1);
        }
        return sorted[index][..Math.Min(64, length)];
    }
}

internal sealed partial class AgentJobStore
{
    private string BuildCatalogRoot => System.IO.Path.Combine(_paths.Results, ".build-results");
    public string ResolveBuildReference(string? reference) => AgentReferenceResolver.Resolve(reference,
        AgentReferenceResolver.Catalog(BuildCatalogRoot, true));
    public string ResolveTestReference(string id, string? reference) => AgentReferenceResolver.Resolve(reference,
        AgentReferenceResolver.Catalog(TestHome(id), false));

    public object ListBuilds(int offset, int limit)
    {
        var values = AgentReferenceResolver.Catalog(BuildCatalogRoot, true);
        return new
        {
            items = values.Select((sha256, index) => new { sha256, reference = AgentReferenceResolver.Display(values, index) })
                .Skip(offset).Take(limit).ToArray(),
            nextOffset = offset + limit < values.Length ? (int?)(offset + limit) : null,
            total = values.Length
        };
    }
}
