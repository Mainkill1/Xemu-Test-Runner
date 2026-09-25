using System.Text.Json.Serialization;
using XemuTestRunner.Runtime;

namespace XemuTestRunner.Networking;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record XisoCategory(string Id, string Name, string[] Tests);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record XisoManifest(int SchemaVersion, string SourceCommit, string Qualification, string IsoSha256,
    string CatalogSha256, string CatalogId, XisoCategory[] Categories, string[][] AtomicGroups, string[] Smoke);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record XisoTemplate(string Id, string Revision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record XisoRegistration(XisoManifest Manifest, string CatalogJson, XisoTemplate Template,
    string IsoAssetId, XisoSettings? Defaults = null);
internal sealed record XisoSuite(string Id, string Revision, XisoRegistration Registration);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record XisoCampaignRequest(string Id, string Application, string? Suite = null, string[]? Categories = null,
    string[]? Tests = null, string Mode = "focused", string? Reference = null, int Repetitions = 1, XisoSettings? Settings = null);
internal sealed record XisoLeaf(string Id, string Name, string Category, string Route, int TimeoutMs, string Isolation);
internal sealed record XisoChunk(string Id, string[] Tests, string[] Groups, string PlanSha256, string ConfigJson);
internal sealed record XisoCampaignPlan(string SuiteId, string SuiteRevision, string IsoSha256, string CatalogId,
    string Planner, object Settings, string[] SelectedTests, string[] AddedDependencies, XisoChunk[] Chunks);
internal sealed record XisoChild(string Id, string Application, string ApplicationIdentity, string Variant,
    string ChunkId, string TestId, string TestRevision);
internal sealed record XisoCampaign(string Id, string RequestIdentity, string Revision, XisoCampaignRequest Request,
    XisoCampaignPlan Plan, XisoChild[] Children, DateTimeOffset CreatedUtc, DateTimeOffset? StartedUtc = null);
