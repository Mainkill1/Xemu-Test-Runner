using System.Text.Json;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Networking;

internal sealed partial class AgentJobStore
{
    private DateTimeOffset _nextBuildIndexScan;
    private string _buildIndexCursor = "";
    private readonly Dictionary<string, string> _buildIndexIssues = new(StringComparer.Ordinal);

    // Called only at an idle boundary. Import failures remain visible and do
    // not change archived evidence or prevent explicitly requested new work.
    public void IndexArchivedBuildResults(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < _nextBuildIndexScan || !Directory.Exists(_paths.Tested)) return;
        _nextBuildIndexScan = DateTimeOffset.UtcNow.AddSeconds(5);
        string[] packages;
        try
        {
            packages = Directory.EnumerateDirectories(_paths.Tested)
                .Where(path => Path.GetFileName(path).StartsWith("agent-", StringComparison.Ordinal))
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal).ToArray();
            lock (_buildIndexIssues) _buildIndexIssues.Remove("archiveCatalog");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            RecordBuildIndexIssue("archiveCatalog", error.Message);
            return;
        }
        if (packages.Length == 0) return;
        var start = Array.FindIndex(packages, path => StringComparer.Ordinal.Compare(Path.GetFileName(path), _buildIndexCursor) > 0);
        if (start < 0) start = 0;
        var attempted = 0;
        for (var scanned = 0; scanned < Math.Min(packages.Length, 256) && attempted < 16; scanned++)
        {
            ct.ThrowIfCancellationRequested();
            var package = packages[(start + scanned) % packages.Length];
            var name = Path.GetFileName(package);
            _buildIndexCursor = name;
            var subject = "package:" + name;
            try
            {
                var id = name[6..];
                if (!IsId(id) || !File.Exists(Path.Combine(Home(id), "request.json"))) continue;
                BuildResultStore.CheckPath(package);
                var journal = AttemptJournal.Read(package);
                if (journal is null || journal.Phase != "finalized" || !BuildResultStore.SafeRunId(journal.RunId)) continue;
                subject = journal.RunId;
                var receipt = Path.Combine(_paths.Results, ".build-results", ".indexed", journal.RunId + ".json");
                BuildResultStore.CheckPath(Path.GetDirectoryName(receipt)!);
                if (File.Exists(receipt)) continue;
                var record = IndexBuildResult(journal.RunId);
                AtomicJson.Write(receipt, new { record.RunId, record.Sha256 });
                attempted++;
                lock (_buildIndexIssues)
                {
                    _buildIndexIssues.Remove(subject);
                    _buildIndexIssues.Remove("package:" + name);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException or AgentRequestException or KeyNotFoundException or InvalidOperationException)
            {
                attempted++;
                RecordBuildIndexIssue(subject, error.Message);
            }
        }
        // The next scan starts after the last visited package. A fixed group of
        // invalid old runs cannot repeatedly consume every import slot.
    }

    private void RecordBuildIndexIssue(string subject, string error)
    {
        lock (_buildIndexIssues)
        {
            if (_buildIndexIssues.Count >= 128 && !_buildIndexIssues.ContainsKey(subject))
                _buildIndexIssues.Remove(_buildIndexIssues.Keys.First());
            _buildIndexIssues[subject] = error;
        }
    }

    public object BuildIndexStatus()
    {
        lock (_buildIndexIssues)
            return new { scope = "archivedApiJobs", nextScanUtc = _nextBuildIndexScan, issues = _buildIndexIssues.Take(10)
                .Select(item => new { subject = item.Key, error = BuildResultStore.Clip(item.Value) }).ToArray(), moreIssues = Math.Max(0, _buildIndexIssues.Count - 10) };
    }
}
