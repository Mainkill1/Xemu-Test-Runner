using System.Globalization;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Networking;

internal sealed record AgentArtifact(string Path, long Bytes, string Href);
internal sealed record AgentArtifactPage(string RunId, string Snapshot, DateTimeOffset CapturedUtc,
    IReadOnlyList<AgentArtifact> Items, string? NextCursor, bool Complete, int Excluded, IReadOnlyList<string> Issues);

/// <summary>Bounded, short-lived metadata snapshots make pagination stable without a database or raw-file reads.</summary>
internal sealed class AgentEvidenceIndex
{
    private sealed record Inventory(string Id, string RunId, DateTimeOffset CapturedUtc,
        IReadOnlyList<AgentArtifact> Items, bool Complete, int Excluded, IReadOnlyList<string> Issues);
    private readonly object _gate = new();
    private readonly Dictionary<string, Inventory> _snapshots = new(StringComparer.Ordinal);
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public AgentArtifactPage List(string root, string runId, string? cursor, int limit, CancellationToken ct)
    {
        lock (_gate)
        {
            foreach (var expired in _snapshots.Values.Where(item => DateTimeOffset.UtcNow - item.CapturedUtc > Lifetime).Select(item => item.Id).ToArray())
                _snapshots.Remove(expired);
            Inventory inventory;
            var offset = 0;
            if (cursor is null)
            {
                inventory = Build(root, runId, ct);
                if (_snapshots.Count >= 4)
                    _snapshots.Remove(_snapshots.Values.MinBy(item => item.CapturedUtc)!.Id);
                _snapshots.Add(inventory.Id, inventory);
            }
            else
            {
                var parts = cursor.Split(':');
                if (cursor.Length > 80 || parts.Length != 2 ||
                    !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out offset) ||
                    !_snapshots.TryGetValue(parts[0], out var existing) || existing.RunId != runId || offset > existing.Items.Count)
                    throw new AgentRequestException(409, "artifact_cursor_invalid", "The artifact snapshot is unknown, expired or belongs to another run.", "Start listing this run again without a cursor; do not treat an interrupted listing as complete.");
                inventory = existing;
            }
            var items = inventory.Items.Skip(offset).Take(limit).ToArray();
            var next = offset + items.Length;
            return new(runId, inventory.Id, inventory.CapturedUtc, items,
                next < inventory.Items.Count ? inventory.Id + ":" + next.ToString(CultureInfo.InvariantCulture) : null,
                inventory.Complete, inventory.Excluded, inventory.Issues);
        }
    }

    private static Inventory Build(string results, string runId, CancellationToken ct)
    {
        var catalog = new EvidenceCatalog(results);
        var root = catalog.Resolve(runId, ".");
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Run evidence does not exist.");
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        var files = new List<AgentArtifact>();
        var issues = new HashSet<string>(StringComparer.Ordinal);
        var excluded = 0;
        var visited = 0;
        var nameCharacters = 0;
        var limited = false;
        while (pending.Count > 0 && !limited)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(directory.Path))
                {
                    ct.ThrowIfCancellationRequested();
                    if (++visited > 50000 || files.Count >= 10000 || nameCharacters > 2 * 1024 * 1024)
                    {
                        issues.Add("inventory_limit");
                        limited = true;
                        break;
                    }
                    try
                    {
                        var attributes = File.GetAttributes(path);
                        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden)) != 0 || System.IO.Path.GetFileName(path).StartsWith('.'))
                        { excluded++; continue; }
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if (directory.Depth >= 16) issues.Add("depth_limit");
                            else pending.Push((path, directory.Depth + 1));
                            continue;
                        }
                        var relative = System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');
                        _ = catalog.Resolve(runId, relative);
                        files.Add(new(relative, new FileInfo(path).Length,
                            "/api/v1/runs/" + Uri.EscapeDataString(runId) + "/artifacts/" + AgentJobStore.EncodePath(relative)));
                        nameCharacters += relative.Length;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                    { issues.Add("entry_unavailable"); }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { issues.Add("directory_unavailable"); }
        }
        return new(Guid.NewGuid().ToString("N"), runId, DateTimeOffset.UtcNow,
            files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray(), issues.Count == 0,
            excluded, issues.OrderBy(issue => issue, StringComparer.Ordinal).ToArray());
    }
}
