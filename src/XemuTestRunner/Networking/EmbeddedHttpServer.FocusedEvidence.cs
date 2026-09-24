namespace XemuTestRunner.Networking;

public sealed partial class EmbeddedHttpServer
{
    private readonly AgentEvidenceIndex _agentEvidence = new();

    private async Task<bool?> TryFocusedEvidenceRouteAsync(Stream stream, HttpRequest request, CancellationToken ct)
    {
        if (request.Method != "GET") return null;
        if (request.Path == "/api/v1/help" && GetQueryValue(request.Query, "topic") == "evidence")
        {
            await WriteAgentJsonAsync(stream, new
            {
                artifacts = "/api/v1/runs/{runId}/artifacts?limit=25&cursor={nextCursor}",
                log = "/api/v1/runs/{runId}/log?file=stderr.log&bytes=4096&cursor={nextCursor}",
                limits = new { page = 100, logBytes = 16384, inventoryFiles = 10000, inventoryDepth = 16, snapshotMinutes = 10 },
                collection = "Follow every page; complete:false or any listing/download failure prevents an all-evidence success claim.",
                logs = "First read is a bounded tail. Cursor reads return only subsequent bytes; reset:true reports detected truncation/rewrite. Decode text is not byte-exact evidence.",
                summary = "/api/v1/runs/{runId}?view=summary"
            }, cancellationToken: ct).ConfigureAwait(false);
            return false;
        }
        const string prefix = "/api/v1/runs/";
        if (!request.Path.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var parts = request.Path[prefix.Length..].Split('/');
        if (parts.Length != 2 || parts[1] is not ("artifacts" or "log")) return null;
        if (!await EnsureOperationAllowedAsync(stream, "bulk_transfer", false, ct).ConfigureAwait(false)) return false;
        var id = Uri.UnescapeDataString(parts[0]);
        using var activity = Activity.TrackTransfer(new { runId = id, action = parts[1] });
        try
        {
            object value;
            if (parts[1] == "artifacts")
                value = _agentEvidence.List(_paths.Results, id, GetQueryValue(request.Query, "cursor"),
                    ReadBoundedQuery(request.Query, "limit", 25, 1, 100), ct);
            else
                value = await AgentLogReader.ReadAsync(_paths.Results, id, GetQueryValue(request.Query, "file") ?? "stdout.log",
                    ReadBoundedQuery(request.Query, "bytes", 4096, 1, 16384), GetQueryValue(request.Query, "cursor"), ct).ConfigureAwait(false);
            await WriteAgentJsonAsync(stream, value, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new AgentRequestException(404, "evidence_not_found", "The requested run or log does not exist.", "Follow the job's run link and list available artifacts before requesting a log.");
        }
        return false;
    }
}
