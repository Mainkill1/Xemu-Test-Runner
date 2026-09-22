using XemuTestRunner.Queue;

namespace XemuTestRunner.Networking;

internal sealed record AgentFile(string Path, long Length, string Sha256, bool Executable = false);
internal sealed record AgentJobRequest(string Id, JobDefinition Job, IReadOnlyList<AgentFile> Files);
internal sealed record AgentJobDocument(AgentJobRequest Request, string CreationHash, DateTimeOffset CreatedUtc);
internal sealed record AgentAction(string Method, string Href, string Purpose);
internal sealed record AgentOperation(
    string Id, string JobId, string Action, string State, DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc = null, int FilesChecked = 0, string? Error = null,
    string? Hint = null, object? Result = null, string? SourceJobId = null)
{
    public string? ErrorCode { get; init; }
}
internal sealed record AgentJobView(
    string Id, string State, string Revision, DateTimeOffset CreatedUtc,
    JobDefinition Job, IReadOnlyList<AgentFile> Files, string? RunId,
    AgentOperation? Operation, IReadOnlyDictionary<string, AgentAction> Actions);

internal sealed class AgentRequestException(
    int status, string code, string message, string hint) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string Hint { get; } = hint;
}
