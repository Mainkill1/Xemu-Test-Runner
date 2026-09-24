using System.Security.Cryptography;
using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Reliability;
using XemuTestRunner.Runtime;

namespace XemuTestRunner.Networking;

internal sealed record AgentNext(string Method, string Href);
internal sealed record AgentBlocker(string Code, string Hint);
internal sealed record AgentOperationBrief(string Id, string Action, string State, int FilesChecked = 0, string? ErrorCode = null);
internal sealed record AgentObservation(bool Ok, string Id, string State, string? RunId,
    string Cursor, bool Changed, AgentNext Next, AgentBlocker? Blocker, AgentOperationBrief? Operation);

internal sealed partial class AgentJobStore
{
    private readonly Dictionary<string, (long Length, long Modified, AgentOperationBrief Value)> _operationObservations = new(StringComparer.Ordinal);

    // The observation path intentionally does not call BuildView/ReadDocument:
    // neither a full job plan nor its payload manifest belongs in a status poll.
    public AgentObservation Observe(string id, RunnerStateSnapshot runner, string epoch)
    {
        if (!File.Exists(System.IO.Path.Combine(Home(id), "request.json")))
            throw new AgentRequestException(404, "job_not_found", "No API job has this ID.", "List API jobs before creating another attempt.");
        var location = Locate(id);
        var state = location.State;
        var operation = ObserveOperation(id, state);
        string? runId = null;
        string? phase = null;
        AgentBlocker? blocker = null;
        try
        {
            var attempt = AttemptJournal.Read(location.Package);
            runId = attempt?.RunId;
            phase = attempt?.Phase;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            blocker = new("attempt_unreadable", "Inspect the existing attempt; do not submit a duplicate.");
        }
        bool busy;
        lock (_gate) { busy = _busy.Contains(id); }
        if (busy && state == "draft") state = "busy";
        if (runner.CurrentJob == id)
        {
            runId = runner.RunId ?? runId;
            phase = runner.Phase;
        }
        if (state == "testing" && (phase == "held" || File.Exists(System.IO.Path.Combine(location.Package, ".runner-recovery.json"))))
        {
            state = "held";
            blocker = new("attempt_held", "Inspect the preserved attempt through the control/result APIs; do not alter queue files.");
        }
        if (state == "queued" && runner.CurrentJob is null && runner.QueueIssue is not null)
        {
            state = "blocked";
            blocker = new("queue_blocked", "Read /api/v1/status for the queue recovery issue. Keep this job ID.");
        }
        if (state == "unavailable")
            blocker = new("job_unavailable", "Package ownership cannot be located. Keep this ID and inspect the runner; do not resubmit.");
        if (state == "draft" && operation?.State is ("failed" or "interrupted"))
            blocker = new(operation.ErrorCode ?? "operation_failed", "Read the operation/validation details and repair this draft.");
        var identity = JsonSerializer.SerializeToUtf8Bytes(new { id, state, runId, phase, blocker, operation });
        var cursor = epoch + ":" + Convert.ToHexString(SHA256.HashData(identity).AsSpan(0, 8)).ToLowerInvariant();
        var self = Url(id) + "?view=summary";
        var next = state switch
        {
            "tested" when runId is not null => "/api/v1/runs/" + Uri.EscapeDataString(runId) + "?view=summary",
            "held" => "/api/v1/control",
            "blocked" => "/api/v1/status",
            "draft" when blocker is not null => Url(id) + "/operation",
            "draft" => Url(id) + "/files",
            _ => self + "&since=" + Uri.EscapeDataString(cursor) + "&wait=20"
        };
        return new(true, id, state, runId, cursor, true, new("GET", next), blocker, operation);
    }

    // Old operation receipts can include a large validation result. Deserialize
    // only the progress fields, once per receipt version, and bound the cache.
    // No payload hashing, artifact enumeration, or hardware sampling occurs here.
    private AgentOperationBrief? ObserveOperation(string id, string packageState)
    {
        var path = OperationPath(id);
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        AgentOperationBrief value;
        var length = info.Length;
        var modified = info.LastWriteTimeUtc.Ticks;
        lock (_gate)
        {
            if (_operationObservations.TryGetValue(id, out var cached) && cached.Length == length && cached.Modified == modified)
                value = cached.Value;
            else
            {
                value = ReadJson<AgentOperationBrief>(path);
                if (_operationObservations.Count >= 128) _operationObservations.Clear();
                _operationObservations[id] = (length, modified, value);
            }
            if (!_runningOperations.Contains(id) && value.State is ("queued" or "running"))
                value = value.Action == "submit" && packageState is ("queued" or "testing" or "tested")
                    ? value with { State = "completed", ErrorCode = null }
                    : value with { State = "interrupted", ErrorCode = "operation_interrupted" };
        }
        return value;
    }
}
