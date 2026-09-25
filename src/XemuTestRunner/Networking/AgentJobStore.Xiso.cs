using System.Text;
using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;
using XemuTestRunner.Runtime;

namespace XemuTestRunner.Networking;

internal sealed partial class AgentJobStore
{
    private readonly object _xisoGate = new();
    private DateTimeOffset _nextXisoResume;
    private string XisoSuiteRoot => Path.Combine(_paths.Pending, ".xiso-suites");
    private string XisoCampaignRoot => Path.Combine(_paths.Pending, ".xiso-campaigns");
    private static string XisoPath(string root, string id, string file)
    {
        if (!IsId(id)) throw new InvalidDataException("Invalid XISO ID.");
        var path = Path.Combine(root, id, file);
        RunStateInventory.NoLinks(path);
        return path;
    }
    private static T XisoRead<T>(string path)
    {
        RunStateInventory.NoLinks(path);
        if (!File.Exists(path)) throw new AgentRequestException(404, "xiso_not_found", "Unknown XISO suite/campaign.", "List /api/v1/xiso-suites or retain the ID returned by campaign creation.");
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("XISO metadata exceeds 4 MiB.");
        return ReadJson<T>(path);
    }

    public object RegisterXisoSuite(string id, XisoRegistration registration)
    {
        var path = XisoPath(XisoSuiteRoot, id, "suite.json");
        _ = XisoPlanner.Validate(registration);
        var template = ReadTest(registration.Template.Id, registration.Template.Revision);
        var job = template.Definition.Job;
        var guest = job.Workload.GuestHddResults ?? throw new InvalidDataException("The saved base test must declare GuestHddResults and its real FATX partition/reference.");
        if (job.SnapshotName is not null || job.RuntimeState.XisoPlan is not null)
            throw new InvalidDataException("Use an ordinary XISO boot template, not a snapshot or generated campaign child.");
        var isolation = job.RuntimeState.Isolation ?? throw new InvalidDataException("The XISO base test needs a managed runtime-state profile.");
        var dvd = isolation.ReadOnlyAssets?.SingleOrDefault(x => x.Field == "dvd_path");
        if (dvd is null || dvd.AssetId != registration.IsoAssetId || dvd.ExpectedSha256 != registration.Manifest.IsoSha256)
            throw new InvalidDataException("Base test dvd_path must pin the ISO asset and digest in this exact suite bundle.");
        var disks = new DiskAssetCatalog(_paths.Workspace);
        var iso = disks.GetRequired(dvd.AssetId);
        if (iso.Kind != "readonly-input" || iso.Sha256 != registration.Manifest.IsoSha256 || !disks.IsReady(iso))
            throw new InvalidDataException("Upload the exact bundled ISO as a readonly-input asset before registration.");
        var seedReference = job.RuntimeState.DiskAssets.SingleOrDefault(x => x.Destination == guest.Image)
            ?? throw new InvalidDataException("Base test HDD must reference one catalog xiso-seed.");
        var seed = disks.GetRequired(seedReference.AssetId);
        if (seed.Kind != "xiso-seed" || seed.Sha256 != seedReference.ExpectedSha256 || !disks.IsReady(seed))
            throw new InvalidDataException("Base test requires an available, pinned clean xiso-seed.");
        var resolved = registration with { Defaults = (registration.Defaults ?? new XisoSettings()).Resolve() };
        var suite = new XisoSuite(id, HashJson(resolved), resolved);
        lock (_xisoGate)
        {
            if (File.Exists(path))
            {
                if (ReadXisoSuite(id).Revision != suite.Revision)
                    throw Conflict("xiso_suite_conflict", "This suite ID is already pinned to another image/catalog/template/default contract.", "Register a new suite name. Existing campaign identities never follow latest.");
            }
            else AtomicJson.Write(path, suite);
        }
        return XisoSuiteView(suite);
    }

    private XisoSuite ReadXisoSuite(string? id)
    {
        if (id is null)
        {
            RunStateInventory.NoLinks(XisoSuiteRoot);
            var candidates = Directory.Exists(XisoSuiteRoot) ? Directory.EnumerateDirectories(XisoSuiteRoot).Take(2).ToArray() : [];
            if (candidates.Length != 1)
                throw new AgentRequestException(409, "xiso_suite_required", "There is not exactly one registered suite.", "Select a suite from /api/v1/xiso-suites; no newest/first version is guessed.");
            id = Path.GetFileName(candidates[0]);
        }
        var suite = XisoRead<XisoSuite>(XisoPath(XisoSuiteRoot, id, "suite.json"));
        if (suite.Id != id || HashJson(suite.Registration) != suite.Revision)
            throw new InvalidDataException("Stored XISO suite identity changed.");
        return suite;
    }
    private static object XisoSuiteView(XisoSuite suite) => new
    {
        id = suite.Id, revision = suite.Revision, suite.Registration.Manifest.SourceCommit,
        suite.Registration.Manifest.Qualification, suite.Registration.Manifest.IsoSha256,
        suite.Registration.Manifest.CatalogId,
        leafCount = suite.Registration.Manifest.Categories.Sum(x => x.Tests.Length),
        categories = suite.Registration.Manifest.Categories.Select(x => new { x.Id, x.Name, tests = x.Tests.Length }).ToArray(),
        defaults = suite.Registration.Defaults,
        tests = "/api/v1/xiso-suites/" + suite.Id + "/tests"
    };
    public object ListXisoSuites(int offset, int limit)
    {
        RunStateInventory.NoLinks(XisoSuiteRoot);
        var suites = Directory.Exists(XisoSuiteRoot) ? Directory.EnumerateDirectories(XisoSuiteRoot)
            .Order(StringComparer.Ordinal).Skip(offset).Take(limit + 1).Select(p => ReadXisoSuite(Path.GetFileName(p))).ToArray() : [];
        return new { items = suites.Take(limit).Select(XisoSuiteView).ToArray(), nextOffset = suites.Length > limit ? (int?)(offset + limit) : null };
    }
    public object XisoTests(string id, string? category, int offset, int limit)
    {
        var suite = ReadXisoSuite(id);
        var tests = XisoPlanner.Validate(suite.Registration);
        if (category is not null)
        {
            if (!suite.Registration.Manifest.Categories.Any(x => x.Id == category)) throw new InvalidDataException("Unknown XISO category: " + category);
            tests = tests.Where(x => x.Category == category).ToArray();
        }
        return new { suite = XisoSuiteView(suite), items = tests.Skip(offset).Take(limit).ToArray(),
            nextOffset = offset + limit < tests.Length ? (int?)(offset + limit) : null };
    }

    public XisoCampaign CreateXisoCampaign(XisoCampaignRequest request)
    {
        if (request.Id.Length > 40) throw new InvalidDataException("Campaign IDs must be at most 40 characters.");
        var path = XisoPath(XisoCampaignRoot, request.Id, "campaign.json");
        var requestIdentity = HashJson(request);
        lock (_xisoGate)
        {
            // Resolve an identical retry from its existing receipt, even if more
            // suites have since been registered or defaults have evolved elsewhere.
            if (File.Exists(path))
            {
                var existing = ReadXisoCampaign(request.Id);
                if (existing.RequestIdentity != requestIdentity)
                    throw Conflict("xiso_campaign_conflict", "The campaign ID belongs to a different request.", "Reuse the original request or choose a new ID for intentional new work.");
                return existing;
            }
            var suite = ReadXisoSuite(request.Suite);
            var plan = XisoPlanner.Resolve(suite, request);
            var template = ReadTest(suite.Registration.Template.Id, suite.Registration.Template.Revision);
            foreach (var application in new[] { request.Application, request.Reference }.Where(x => x is not null).Distinct())
            {
                var app = ReadDocument(application!);
                RequireStableSource(Locate(application!).State);
                ValidateApplicationPayload(template.Definition, app.Request);
            }
            var children = new List<XisoChild>();
            foreach (var chunk in plan.Chunks)
            {
                var baked = SaveXisoChildDefinition(template, plan, chunk);
                for (var repetition = 1; repetition <= request.Repetitions; repetition++)
                {
                    var schedule = request.Reference is null ? new[] { (Variant: "B", App: request.Application) } :
                        new[] { (Variant: "A1", App: request.Reference), (Variant: "B1", App: request.Application),
                                (Variant: "B2", App: request.Application), (Variant: "A2", App: request.Reference) };
                    foreach (var (variant, application) in schedule)
                    {
                        var childId = $"{request.Id}-{chunk.Id}-r{repetition}-{variant.ToLowerInvariant()}";
                        children.Add(new(childId, application!, ReadDocument(application!).CreationHash, variant,
                            chunk.Id, baked.Definition.Id, baked.Revision));
                    }
                }
            }
            // Publish the complete schedule before creating subordinate requests.
            // A retry/restart repairs missing receipts under the SAME IDs.
            var campaign = new XisoCampaign(request.Id, requestIdentity, HashJson(new { requestIdentity, plan, children }),
                request, plan, children.ToArray(), DateTimeOffset.UtcNow);
            AtomicJson.Write(path, campaign);
            EnsureXisoChildren(campaign, authorize: false);
            return campaign;
        }
    }

    private AgentBakedTest SaveXisoChildDefinition(AgentBakedTest template, XisoCampaignPlan plan, XisoChunk chunk)
    {
        var job = JsonSerializer.Deserialize<JobDefinition>(JsonSerializer.SerializeToUtf8Bytes(template.Definition.Job, ConfigLoader.JsonOptions), ConfigLoader.JsonOptions)!;
        var id = "xiso-" + plan.SuiteRevision[..12] + "-" + chunk.PlanSha256[..16];
        job.Id = id;
        job.Plan.Clear(); // The XISO itself consumes the resolved plan and shuts down.
        job.SnapshotName = null;
        var guest = job.Workload.GuestHddResults!;
        job.RuntimeState.XisoPlan = new XisoPlanDefinition
        {
            Image = guest.Image, PartitionOffsetBytes = guest.PartitionOffsetBytes,
            PartitionLengthBytes = guest.PartitionLengthBytes, CatalogId = plan.CatalogId,
            PlanSha256 = chunk.PlanSha256, ConfigJson = chunk.ConfigJson,
            ConfigSha256 = XisoHash.Bytes(Encoding.UTF8.GetBytes(chunk.ConfigJson)), Leaves = chunk.Tests, Groups = chunk.Groups
        };
        job.RuntimeState.XisoPlan.Validate();
        var hdd = job.RuntimeState.DiskAssets.Single(x => x.Destination == guest.Image);
        hdd.Retention = "deleteAfterEvidence";
        var definition = template.Definition with { Id = id, Job = job, Description = "XISO " + chunk.Id + " / " + chunk.Tests.Length + " leaves" };
        var baked = new AgentBakedTest(HashJson(definition), definition, DateTimeOffset.UtcNow);
        lock (_testLibraryGate)
        {
            var home = TestHome(id);
            Directory.CreateDirectory(home);
            var path = Path.Combine(home, baked.Revision + ".json");
            if (!File.Exists(path)) AtomicJson.Write(path, baked);
            else _ = ReadTest(id, baked.Revision);
            AtomicJson.Write(Path.Combine(home, baked.Revision + ".summary.json"), TestSummary(baked));
        }
        return baked;
    }

    public XisoCampaign ReadXisoCampaign(string id)
    {
        var value = XisoRead<XisoCampaign>(XisoPath(XisoCampaignRoot, id, "campaign.json"));
        if (value.Id != id || HashJson(value.Request) != value.RequestIdentity ||
            HashJson(new { requestIdentity = value.RequestIdentity, plan = value.Plan, children = value.Children }) != value.Revision)
            throw new InvalidDataException("Stored XISO campaign identity changed.");
        return value;
    }
    public XisoCampaign StartXisoCampaign(string id)
    {
        lock (_xisoGate)
        {
            var value = ReadXisoCampaign(id);
            if (value.StartedUtc is null)
            {
                value = value with { StartedUtc = DateTimeOffset.UtcNow };
                AtomicJson.Write(XisoPath(XisoCampaignRoot, id, "campaign.json"), value);
            }
            EnsureXisoChildren(value, authorize: true);
            return value;
        }
    }
    private void EnsureXisoChildren(XisoCampaign campaign, bool authorize)
    {
        foreach (var child in campaign.Children)
        {
            if (ReadDocument(child.Application).CreationHash != child.ApplicationIdentity)
                throw new InvalidDataException("A campaign application identity changed.");
            var request = CreateRequestedTest(new TestRunRequest(child.Id, child.Application, child.TestId, child.TestRevision));
            if (authorize && request.StartRequestedUtc is null && request.State == "uploaded") RequestTestStart(child.Id);
        }
        if (authorize) AtomicJson.Write(XisoPath(XisoCampaignRoot, campaign.Id, "published.json"), new { campaign.Revision });
    }
    public void ResumeXisoCampaignStarts()
    {
        if (DateTimeOffset.UtcNow < _nextXisoResume) return;
        _nextXisoResume = DateTimeOffset.UtcNow.AddSeconds(5);
        RunStateInventory.NoLinks(XisoCampaignRoot);
        if (!Directory.Exists(XisoCampaignRoot)) return;
        foreach (var home in Directory.EnumerateDirectories(XisoCampaignRoot).Take(10000))
        {
            var id = Path.GetFileName(home);
            if (File.Exists(XisoPath(XisoCampaignRoot, id, "published.json"))) continue;
            try
            {
                lock (_xisoGate)
                {
                    var campaign = ReadXisoCampaign(id);
                    if (campaign.StartedUtc is not null) EnsureXisoChildren(campaign, authorize: true);
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or AgentRequestException)
            { System.Diagnostics.Trace.TraceError("XISO campaign reconciliation " + id + ": " + error.Message); }
        }
    }
}
