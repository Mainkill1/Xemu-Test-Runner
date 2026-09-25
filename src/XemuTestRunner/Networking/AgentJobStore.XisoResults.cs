namespace XemuTestRunner.Networking;

internal sealed record XisoChildState(string Id, string Variant, string Chunk, string State, string? RunId,
    bool AssessmentAvailable, AgentOutcome? Outcome, string? Code, string? Result);
internal sealed record XisoCampaignState(string Id, string Revision, string State, bool StartRequested,
    string Suite, string CatalogId, int SelectedLeaves, int Chunks, int Children, int Terminal,
    int Passed, int Crashed, int Failed, int Attention, int MissingAssessments,
    IReadOnlyList<XisoChildState> Items, int MoreItems, string Plan, string Detail);

internal sealed partial class AgentJobStore
{
    private readonly AgentAssessmentReader _xisoAssessment = new();
    public XisoCampaignState ObserveXisoCampaign(string id, int offset = 0, int limit = 5)
    {
        var campaign = ReadXisoCampaign(id);
        var items = new List<XisoChildState>();
        var terminal = 0; var passed = 0; var crashed = 0; var failed = 0; var attention = 0; var missing = 0;
        foreach (var child in campaign.Children)
        {
            try
            {
                var request = ReadRequestedTest(child.Id);
                var isTerminal = request.State is "tested" or "failed" or "cancelled";
                if (isTerminal) terminal++;
                if (request.State is "failed" or "cancelled") failed++;
                if (request.State is "held" or "waitingForUpload") attention++;
                AgentOutcome? outcome = null;
                var available = false;
                if (request.State == "tested" && request.RunId is not null)
                {
                    var assessment = _xisoAssessment.Read(_paths.Results, request.RunId);
                    available = assessment.Available;
                    outcome = assessment.Outcome;
                    if (!available) missing++;
                    else if (outcome?.Execution == "crashed") crashed++;
                    else if (outcome?.Execution == "completed" && outcome.Correctness == "passed" && outcome.Evidence == "complete") passed++;
                    else failed++;
                }
                else if (request.State == "tested") missing++;
                items.Add(new(child.Id, child.Variant, child.ChunkId, request.State, request.RunId, available, outcome,
                    request.ErrorCode, request.RunId is null ? null : "/api/v1/runs/" + Uri.EscapeDataString(request.RunId) + "?view=summary"));
            }
            catch (Exception error) when (error is AgentRequestException or IOException or InvalidDataException)
            {
                attention++;
                items.Add(new(child.Id, child.Variant, child.ChunkId, "unavailable", null, false, null,
                    error is AgentRequestException a ? a.Code : "child_metadata_unavailable", null));
            }
        }
        var state = campaign.StartedUtc is null ? "created" : terminal == campaign.Children.Length ?
            passed == campaign.Children.Length ? "passed" : "finishedWithIssues" : attention > 0 ? "attention" : "running";
        return new(campaign.Id, campaign.Revision, state, campaign.StartedUtc is not null,
            campaign.Plan.SuiteId, campaign.Plan.CatalogId, campaign.Plan.SelectedTests.Length,
            campaign.Plan.Chunks.Length, campaign.Children.Length, terminal, passed, crashed, failed, attention, missing,
            items.Skip(offset).Take(limit).ToArray(), Math.Max(0, items.Count - offset - limit),
            "/api/v1/xiso-campaigns/" + id + "?view=plan", "/api/v1/xiso-campaigns/" + id + "?limit=100&offset=" + (offset + limit));
    }
    public object ListXisoCampaigns(int offset, int limit)
    {
        RunStateInventory.NoLinks(XisoCampaignRoot);
        var ids = Directory.Exists(XisoCampaignRoot) ? Directory.EnumerateDirectories(XisoCampaignRoot)
            .Order(StringComparer.Ordinal).Skip(offset).Take(limit + 1).Select(Path.GetFileName).ToArray() : [];
        return new { items = ids.Take(limit).Select(id => ObserveXisoCampaign(id!, limit: 0)).ToArray(),
            nextOffset = ids.Length > limit ? (int?)(offset + limit) : null };
    }
}
