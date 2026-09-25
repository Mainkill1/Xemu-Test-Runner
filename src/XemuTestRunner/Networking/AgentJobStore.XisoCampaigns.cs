using System.Text.Json;
using System.Text.Json.Serialization;
using XemuTestRunner.Config;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;
using XemuTestRunner.Runtime;

namespace XemuTestRunner.Networking;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record XisoCampaignRequest(string Id, string Application, string? Suite = null,
    string[]? Categories = null, string[]? Tests = null, string? Mode = null,
    XisoSettings? Settings = null, string? ReferenceApplication = null);
internal sealed record XisoChunk(int Index, string TestId, string Revision, string PlanId,
    string[] Tests, string[] Categories, RuntimeStateDefinition RuntimeState);
internal sealed record XisoAttempt(string Id, string Application, string ApplicationIdentity,
    int Chunk, string Label, string Variant);
internal sealed record XisoCampaignPlan(string Id, string Suite, string SuiteRevision,
    string IsoSha256, string CatalogId, string Qualification, string Mode, XisoSettings Settings,
    string[] Tests, string[] AddedDependencies, XisoChunk[] Chunks, XisoAttempt[] Attempts);
internal sealed record XisoCampaign(XisoCampaignRequest Request, string RequestIdentity,
    string Revision, XisoCampaignPlan Plan, DateTimeOffset CreatedUtc,
    DateTimeOffset? StartRequestedUtc = null, bool CancelRequested = false, string? Error = null);
internal sealed record XisoAttemptStatus(string Id, string Label, string Variant, int Chunk,
    string State, string? RunId, bool Terminal, string? Execution, string? Correctness,
    string? Evidence, string? Comparison, string? Error);
internal sealed record XisoCampaignStatus(string Id, string Revision, string State, string Event,
    bool Terminal, bool StartRequested, int SelectedLeaves, int ChunkCount, int AttemptCount,
    int Finished, int Passed, int Failed, int Incomplete, int Remaining,
    string? Active, string? Error, string Next, string Details);

internal sealed partial class AgentJobStore
{
    private string XisoCampaignRoot => System.IO.Path.Combine(_paths.Pending, ".xiso-campaigns");

    public object CreateXisoCampaign(XisoCampaignRequest request)
    {
        if (!IsId(request.Id) || request.Id.Length > 38) throw new InvalidDataException("Campaign ID must be a runner ID of at most 38 characters.");
        request = request with {
            Categories = (request.Categories ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            Tests = (request.Tests ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
        };
        var identity = HashJson(request);
        lock (_xisoGate)
        {
            var path = XisoCampaignPath(request.Id);
            if (File.Exists(path))
            {
                var current = ReadXisoCampaign(request.Id);
                if (current.RequestIdentity != identity) throw Conflict("xiso_campaign_conflict", "This campaign ID already represents a different request.", "Use a new ID for changed inputs. Repeating an identical request never adds more tests.");
                return ObserveXisoCampaign(current);
            }
            var suite = ReadXisoSuite(request.Suite);
            var selection = suite.Data.Catalog.Select(request.Categories, request.Tests, request.Mode);
            var settings = (request.Settings ?? new XisoSettings()).Resolve(suite.Data.Settings);
            var application = ReadDocument(request.Application);
            RequireStableSource(Locate(request.Application).State);
            ValidateApplicationPayload(suite.Data.Template, application.Request);
            string? referenceIdentity = null;
            if (request.ReferenceApplication is not null)
            {
                var reference = ReadDocument(request.ReferenceApplication);
                RequireStableSource(Locate(request.ReferenceApplication).State);
                ValidateApplicationPayload(suite.Data.Template, reference.Request);
                referenceIdentity = reference.CreationHash;
            }
            if (selection.Chunks.Length * (referenceIdentity is null ? 1 : 4) > 256)
                throw new InvalidDataException("Campaign exceeds 256 attempts; narrow its selected categories.");
            var chunks = selection.Chunks.Select((leaves, index) => BakeXisoChunk(suite, leaves, settings, index + 1)).ToArray();
            var attempts = new List<XisoAttempt>();
            foreach (var chunk in chunks)
            {
                if (request.ReferenceApplication is null)
                    Add(request.Application, application.CreationHash, chunk.Index, "B1", "candidate");
                else
                {
                    Add(request.ReferenceApplication, referenceIdentity!, chunk.Index, "A1", "reference");
                    Add(request.Application, application.CreationHash, chunk.Index, "B1", "candidate");
                    Add(request.Application, application.CreationHash, chunk.Index, "B2", "candidate");
                    Add(request.ReferenceApplication, referenceIdentity!, chunk.Index, "A2", "reference");
                }
            }
            var plan = new XisoCampaignPlan(request.Id, suite.Data.Id, suite.Revision, suite.Data.IsoSha256,
                suite.Data.Catalog.Id, suite.Data.Qualification, selection.Mode, settings,
                selection.Leaves.Select(x => x.Id).ToArray(), selection.AddedDependencies, chunks, attempts.ToArray());
            var value = new XisoCampaign(request, identity, HashJson(plan), plan, DateTimeOffset.UtcNow);
            // The complete schedule is durable before any child acquires execution intent.
            AtomicJson.Write(path, value);
            return ObserveXisoCampaign(value);
            void Add(string id, string hash, int chunk, string label, string variant) =>
                attempts.Add(new("xc-" + request.Id + "-" + (attempts.Count + 1).ToString("D3"), id, hash, chunk, label, variant));
        }
    }

    private XisoChunk BakeXisoChunk(XisoSuite suite, XisoLeaf[] leaves, XisoSettings settings, int index)
    {
        var source = suite.Data.Template;
        var job = JsonSerializer.Deserialize<JobDefinition>(JsonSerializer.Serialize(source.Job, ConfigLoader.JsonOptions), ConfigLoader.JsonOptions)!;
        var extraction = job.Workload.GuestHddResults!;
        var ids = leaves.Select(x => x.Id).ToArray();
        var groups = suite.Data.Catalog.Groups.Where(group => group.Children.Any(child => ids.Contains(child, StringComparer.Ordinal)))
            .Select(x => x.Id).ToArray();
        var execution = new XisoExecution(extraction.Image, extraction.PartitionOffsetBytes, extraction.PartitionLengthBytes,
            suite.Revision, suite.Data.IsoSha256, suite.Data.Catalog.Id, ids, groups, settings);
        var id = "xiso-" + execution.PlanId[7..39];
        job.Id = id;
        job.RuntimeState.Xiso = execution;
        extraction.Xiso = execution;
        // No automatic input/menu control is necessary: the guest consumes the plan
        // at boot and requests shutdown. Preserve deliberate template diagnostics.
        job.StartPaused = false;
        job.SnapshotName = null;
        job.Workload.RequirePlanCompletion = true;
        var definition = source with { Id = id, Job = job, Description = "XISO " + suite.Data.Id + " chunk " + index };
        var revision = HashJson(definition);
        var baked = new AgentBakedTest(revision, definition, DateTimeOffset.UtcNow);
        lock (_testLibraryGate)
        {
            var home = TestHome(id); Directory.CreateDirectory(home);
            var path = System.IO.Path.Combine(home, revision + ".json");
            if (File.Exists(path)) _ = ReadTest(id, revision);
            else AtomicJson.Write(path, baked);
            AtomicJson.Write(System.IO.Path.Combine(home, revision + ".summary.json"), TestSummary(baked));
        }
        return new(index, id, revision, execution.PlanId, ids, leaves.Select(x => x.Category).Distinct().ToArray(), job.RuntimeState);
    }

    public object StartXisoCampaign(string id)
    {
        lock (_xisoGate)
        {
            var value = ReadXisoCampaign(id);
            if (value.CancelRequested || value.Error is not null) throw Conflict("xiso_campaign_terminal", "This campaign was cancelled or failed preparation.", "Read the retained report; an intentional retry needs a new ID.");
            if (value.StartRequestedUtc is null)
            {
                value = value with { StartRequestedUtc = DateTimeOffset.UtcNow };
                AtomicJson.Write(XisoCampaignPath(id), value);
            }
            return ObserveXisoCampaign(value);
        }
    }

    public object CancelXisoCampaign(string id)
    {
        lock (_xisoGate)
        {
            var value = ReadXisoCampaign(id) with { CancelRequested = true };
            AtomicJson.Write(XisoCampaignPath(id), value);
            // In-flight process ownership is not bypassed. Cancel only an unprepared
            // child; leave an already-running/claimed attempt to finalize normally.
            foreach (var child in value.Plan.Attempts)
            {
                if (!File.Exists(RequestedPath(child.Id))) continue;
                try { _ = CancelRequestedTest(child.Id); }
                catch (AgentRequestException) { }
            }
            return ObserveXisoCampaign(value);
        }
    }

    // Invoked by the existing idle dispatcher, never by a status/read request.
    public void AdvanceXisoCampaigns()
    {
        if (!Directory.Exists(XisoCampaignRoot)) return;
        lock (_xisoGate)
        {
            var values = Directory.EnumerateFiles(XisoCampaignRoot, "*.json")
                .Select(path => ReadXisoCampaign(System.IO.Path.GetFileNameWithoutExtension(path)))
                .Where(x => x.StartRequestedUtc is not null && !x.CancelRequested && x.Error is null)
                .OrderBy(x => x.StartRequestedUtc).ThenBy(x => x.Plan.Id, StringComparer.Ordinal);
            foreach (var campaign in values)
            {
                foreach (var attempt in campaign.Plan.Attempts)
                {
                    var current = ObserveXisoAttempt(attempt);
                    if (current.Terminal) continue;
                    if (current.State != "uncreated") return;
                    try
                    {
                        var application = ReadDocument(attempt.Application);
                        if (application.CreationHash != attempt.ApplicationIdentity) throw new InvalidDataException("Pinned application identity changed before dispatch.");
                        var chunk = campaign.Plan.Chunks.Single(x => x.Index == attempt.Chunk);
                        _ = CreateRequestedTest(new(attempt.Id, attempt.Application, chunk.TestId, chunk.Revision));
                        _ = RequestTestStart(attempt.Id);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or AgentRequestException or JsonException)
                    {
                        AtomicJson.Write(XisoCampaignPath(campaign.Plan.Id), campaign with { Error = error.Message });
                    }
                    return;
                }
            }
        }
    }

    public XisoCampaignStatus XisoCampaignStatus(string id) { lock (_xisoGate) return ObserveXisoCampaign(ReadXisoCampaign(id)); }
    public object XisoCampaignPlan(string id) { lock (_xisoGate) return ReadXisoCampaign(id).Plan; }
    public object XisoCampaignAttempts(string id, int offset, int limit)
    {
        lock (_xisoGate)
        {
            var plan = ReadXisoCampaign(id).Plan;
            return new { items = plan.Attempts.Skip(offset).Take(limit).Select(ObserveXisoAttempt).ToArray(),
                nextOffset = offset + limit < plan.Attempts.Length ? (int?)(offset + limit) : null };
        }
    }
    public object ListXisoCampaigns(int offset, int limit)
    {
        lock (_xisoGate)
        {
            RunStateInventory.NoLinks(XisoCampaignRoot);
            var paths = Directory.Exists(XisoCampaignRoot) ? Directory.EnumerateFiles(XisoCampaignRoot, "*.json").Order(StringComparer.Ordinal).Skip(offset).Take(limit + 1).ToArray() : [];
            return new { items = paths.Take(limit).Select(path => ObserveXisoCampaign(ReadXisoCampaign(System.IO.Path.GetFileNameWithoutExtension(path)))).ToArray(),
                nextOffset = paths.Length > limit ? (int?)(offset + limit) : null };
        }
    }

    private XisoAttemptStatus ObserveXisoAttempt(XisoAttempt attempt)
    {
        if (!File.Exists(RequestedPath(attempt.Id))) return new(attempt.Id, attempt.Label, attempt.Variant, attempt.Chunk, "uncreated", null, false, null, null, null, null, null);
        var requested = ReadRequestedTest(attempt.Id);
        var terminal = requested.State is "tested" or "failed" or "cancelled";
        AgentOutcome? outcome = null;
        string? error = requested.Error;
        if (requested.RunId is not null && terminal)
        {
            var assessment = new AgentAssessmentReader().Read(_paths.Results, requested.RunId);
            outcome = assessment.Outcome;
            if (!assessment.Available) error = assessment.Code;
        }
        return new(attempt.Id, attempt.Label, attempt.Variant, attempt.Chunk, requested.State, requested.RunId, terminal,
            outcome?.Execution, outcome?.Correctness, outcome?.Evidence, outcome?.Comparison, error);
    }
    private XisoCampaignStatus ObserveXisoCampaign(XisoCampaign campaign)
    {
        var attempts = campaign.Plan.Attempts.Select(ObserveXisoAttempt).ToArray();
        var finished = attempts.Count(x => x.Terminal);
        var passed = attempts.Count(x => x.Execution == "completed" && x.Correctness == "passed" && x.Evidence == "complete");
        var failed = attempts.Count(x => x.State == "failed" || x.Execution is "crashed" or "failed" or "timedOut" || x.Correctness == "failed");
        var active = attempts.FirstOrDefault(x => !x.Terminal && x.State != "uncreated");
        var terminal = finished == attempts.Length || (campaign.CancelRequested && active is null) || (campaign.Error is not null && active is null);
        var state = campaign.CancelRequested ? (terminal ? "cancelled" : "cancelling") : campaign.Error is not null ? "failed" :
            terminal ? (passed == attempts.Length ? "passed" : failed > 0 ? "failed" : "incomplete") :
            campaign.StartRequestedUtc is null ? "uploaded" : active?.State == "held" ? "held" : active is null ? "queued" : "running";
        var attention = state is "uploaded" or "held" || active?.State == "waitingForUpload" || campaign.Error is not null;
        return new(campaign.Plan.Id, campaign.Revision, state, terminal ? "finished" : attention ? "attention" : "heartbeat",
            terminal, campaign.StartRequestedUtc is not null, campaign.Plan.Tests.Length, campaign.Plan.Chunks.Length,
            attempts.Length, finished, passed, failed, Math.Max(0, finished - passed - failed), attempts.Length - finished,
            active?.Id, campaign.Error ?? active?.Error, "/api/v1/xiso-campaigns/" + campaign.Plan.Id + "/wait",
            "/api/v1/xiso-campaigns/" + campaign.Plan.Id + "/attempts");
    }
    private string XisoCampaignPath(string id)
    {
        if (!IsId(id) || id.Length > 38) throw new InvalidDataException("Invalid XISO campaign ID.");
        RunStateInventory.NoLinks(XisoCampaignRoot);
        var path = System.IO.Path.Combine(XisoCampaignRoot, id + ".json");
        RunStateInventory.NoLinks(path); return path;
    }
    private XisoCampaign ReadXisoCampaign(string id)
    {
        var path = XisoCampaignPath(id);
        if (!File.Exists(path)) throw new AgentRequestException(404, "xiso_campaign_not_found", "Unknown XISO campaign.", "Create a campaign without starting it, or list existing campaigns.");
        var value = ReadJson<XisoCampaign>(path);
        if (value.Plan.Id != id || HashJson(value.Plan) != value.Revision || HashJson(value.Request) != value.RequestIdentity)
            throw new InvalidDataException("Stored campaign identity does not match its immutable plan.");
        return value;
    }
}
