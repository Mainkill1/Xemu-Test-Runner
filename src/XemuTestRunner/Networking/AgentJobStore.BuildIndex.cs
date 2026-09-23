using System.Text.Json;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Networking;

internal sealed partial class AgentJobStore
{
    private DateTimeOffset _nextBuildIndexScan;
    private readonly Dictionary<string, string> _buildIndexIssues = new(StringComparer.Ordinal);

    // Called only at an idle boundary. Results are indexed from the tester's
    // canonical evidence, not uploaded agent calculations. Ordinary API jobs and
    // requested tests share this path; already indexed runs are left immutable.
    public void IndexArchivedBuildResults(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < _nextBuildIndexScan || !Directory.Exists(_paths.Tested)) return;
        _nextBuildIndexScan = DateTimeOffset.UtcNow.AddSeconds(5);
        var processed = 0;
        foreach (var package in Directory.EnumerateDirectories(_paths.Tested))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(package);
            if (!name.StartsWith("agent-", StringComparison.Ordinal)) continue;
            var id = name[6..];
            if (!IsId(id) || !File.Exists(Path.Combine(Home(id), "request.json"))) continue;
            var journal = AttemptJournal.Read(package);
            if (journal is null || journal.Phase != "finalized" || !BuildResultStore.SafeRunId(journal.RunId)) continue;
            var receipt = Path.Combine(_paths.Results, ".build-results", ".indexed", journal.RunId + ".json");
            BuildResultStore.CheckPath(Path.GetDirectoryName(receipt)!);
            if (File.Exists(receipt)) continue;
            if (++processed > 16) break;
            try
            {
                var record = IndexBuildResult(journal.RunId);
                AtomicJson.Write(receipt, new { record.RunId, record.Sha256 });
                lock (_buildIndexIssues) _buildIndexIssues.Remove(journal.RunId);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException or AgentRequestException or KeyNotFoundException or InvalidOperationException)
            {
                lock (_buildIndexIssues)
                {
                    if (_buildIndexIssues.Count >= 128) _buildIndexIssues.Clear();
                    _buildIndexIssues[journal.RunId] = error.Message;
                }
            }
        }
    }

    public object BuildIndexStatus()
    {
        lock (_buildIndexIssues)
            return new { scope = "archivedApiJobs", nextScanUtc = _nextBuildIndexScan, issues = _buildIndexIssues.Take(10)
                .Select(item => new { runId = item.Key, error = BuildResultStore.Clip(item.Value) }).ToArray(), moreIssues = Math.Max(0, _buildIndexIssues.Count - 10) };
    }
}
