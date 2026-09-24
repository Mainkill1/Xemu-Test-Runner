using System.Text.Json;

namespace XemuTestRunner.Networking;

public sealed partial class EmbeddedHttpServer
{
    private async Task RunRequestedTestsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var state = _state.Snapshot();
                if (state.Phase == "idle" && state.CurrentJob is null && state.QueueIssue is null && !_control.HasActiveSession &&
                    !HasVisiblePackage(_paths.Pending) && !HasVisiblePackage(_paths.Testing))
                {
                    AgentJobs.IndexArchivedBuildResults(ct);
                    await AgentJobs.DispatchRequestedTestAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            { System.Diagnostics.Trace.TraceError("Requested-test dispatcher: " + error); }
            try { await Task.Delay(250, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    private static bool HasVisiblePackage(string root) => Directory.Exists(root) &&
        Directory.EnumerateDirectories(root).Any(path => !Path.GetFileName(path).StartsWith('.'));

    private async Task<bool?> TryRequestedTestRoutesAsync(Stream stream, HttpRequest request, CancellationToken ct)
    {
        if (request.Path == "/tests" && request.Method == "GET")
        {
            await WriteHtmlAsync(stream, TestConfigurationPage.Html, false, ct).ConfigureAwait(false);
            return false;
        }
        if (request.Path == "/api/v1/help" && GetQueryValue(request.Query, "topic") == "test-workflow" && request.Method == "GET")
        {
            await WriteAgentJsonAsync(stream, new {
                version = 1, capabilities = new[] { "namedConfigs", "requestedTests", "uploadOnly", "executableHashResults", "pinnedBaseline", "serverComparison", "applicationIdentity", "performanceAnalysis" },
                configs = "/api/v1/test-configs", viewer = "/tests", requests = "/api/v1/test-runs", results = "/api/v1/help?topic=build-results",
                application = "/api/v1/applications/{id}", performance = "/api/v1/runs/{runId}/performance",
                start = "POST /api/v1/test-runs/{id}/start", rule = "Uploads never start tests. Explicit start persists intent and queues behind current work."
            }, cancellationToken: ct).ConfigureAwait(false);
            return false;
        }
        if (request.Path == "/api/v1/test-configs" && request.Method == "GET")
        {
            await WriteAgentJsonAsync(stream, AgentJobs.ListTests(ReadBoundedQuery(request.Query, "offset", 0, 0, 1000000),
                ReadBoundedQuery(request.Query, "limit", 25, 1, 100)), cancellationToken: ct).ConfigureAwait(false);
            return false;
        }
        const string configs = "/api/v1/test-configs/";
        if (request.Path.StartsWith(configs, StringComparison.Ordinal))
        {
            var parts = request.Path[configs.Length..].Split('/');
            if (parts.Length == 1 && request.Method == "POST")
            {
                if (!await EnsureOperationAllowedAsync(stream, "bulk_transfer", false, ct).ConfigureAwait(false)) return false;
                var body = await ReadAgentBodyAsync<TestConfigUpload>(stream, request, ct).ConfigureAwait(false);
                await WriteAgentJsonAsync(stream, AgentJobs.UploadConfig(Uri.UnescapeDataString(parts[0]), body), cancellationToken: ct).ConfigureAwait(false);
                return false;
            }
            if (parts.Length == 2 && request.Method == "GET")
            {
                if (!await EnsureOperationAllowedAsync(stream, "bulk_transfer", false, ct).ConfigureAwait(false)) return false;
                await WriteAgentJsonAsync(stream, AgentJobs.DescribeTest(Uri.UnescapeDataString(parts[0]), parts[1], true), cancellationToken: ct).ConfigureAwait(false);
                return false;
            }
        }
        const string runs = "/api/v1/test-runs";
        if (request.Path == runs)
        {
            if (request.Method == "POST")
            {
                var body = await ReadAgentBodyAsync<TestRunRequest>(stream, request, ct).ConfigureAwait(false);
                await WriteAgentJsonAsync(stream, AgentJobs.RequestedView(AgentJobs.CreateRequestedTest(body)), cancellationToken: ct).ConfigureAwait(false);
                return false;
            }
            if (request.Method == "GET")
            {
                await WriteAgentJsonAsync(stream, AgentJobs.ListRequestedTests(ReadBoundedQuery(request.Query, "offset", 0, 0, 1000000),
                    ReadBoundedQuery(request.Query, "limit", 25, 1, 100)), cancellationToken: ct).ConfigureAwait(false);
                return false;
            }
        }
        if (request.Path.StartsWith(runs + "/", StringComparison.Ordinal))
        {
            var parts = request.Path[(runs.Length + 1)..].Split('/');
            var id = Uri.UnescapeDataString(parts[0]);
            if (parts.Length == 1 && request.Method is "GET" or "DELETE")
            {
                var value = request.Method == "GET" ? AgentJobs.ReadRequestedTest(id) : AgentJobs.CancelRequestedTest(id);
                await WriteAgentJsonAsync(stream, AgentJobs.RequestedView(value), cancellationToken: ct).ConfigureAwait(false);
                return false;
            }
            if (parts.Length == 2 && parts[1] == "start" && request.Method == "POST")
            {
                if (request.ContentLength.GetValueOrDefault() > 0)
                {
                    var body = await ReadAgentBodyAsync<JsonElement>(stream, request, ct).ConfigureAwait(false);
                    if (body.ValueKind != JsonValueKind.Object || body.EnumerateObject().Any()) throw new InvalidDataException("Start accepts no body or an empty JSON object.");
                }
                await WriteAgentJsonAsync(stream, AgentJobs.RequestedView(AgentJobs.RequestTestStart(id)), 202, cancellationToken: ct).ConfigureAwait(false);
                return false;
            }
        }
        return null;
    }
}
