using System.Text.Json.Serialization;
using XemuTestRunner.Queue;

namespace XemuTestRunner.Networking;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AgentBakeRequest(string SourceJobId, string? Description = null, IReadOnlyList<string>? BuildFiles = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AgentTestRunRequest(string Id, string TestId, string? Revision,
    IReadOnlyList<AgentFile>? Files = null, string? ExperimentId = null, string? Variant = null, string? Reference = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AgentReuseRequest(string SourceJobId);

internal sealed record AgentTestDefinition(string Id, string SourceJobId, string Description,
    JobDefinition Job, IReadOnlyList<AgentFile> Files, IReadOnlyList<string> BuildFiles);
internal sealed record AgentBakedTest(string Revision, AgentTestDefinition Definition, DateTimeOffset CreatedUtc);
internal sealed record AgentTestSummary(string Id, string Revision, string Description, string SourceJobId,
    int StepCount, int FileCount, int BuildFileCount, string Self);
