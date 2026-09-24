namespace XemuTestRunner.Networking;

public sealed partial class EmbeddedHttpServer
{
    private async Task<bool?> TryTestLibraryRouteAsync(Stream stream, HttpRequest request, CancellationToken ct)
    {
        if (request.Method == "GET" && request.Path == "/api/v1/help" && GetQueryValue(request.Query, "topic") == "tests")
        {
            await WriteAgentJsonAsync(stream, new
            {
                catalog = "/api/v1/tests",
                bake = new { method = "POST", path = "/api/v1/tests/{id}/bake", body = new { sourceJobId = "uploaded-seed" } },
                create = new { method = "POST", path = "/api/v1/jobs/from-test", body = new { id = "new-attempt", testId = "smoke", revision = "required-64-hex-content-revision" } },
                changedBuild = "Optional Files declarations replace only baked buildFiles. Unchanged payload is verified/copied locally. Upload remaining files, then submit normally.",
                retry = "Reuse the identical request and ID after a lost response. A fresh attempt needs a fresh ID.",
                validity = "Baking stores a definition, not a test-pass verdict. Workload/assertion/policy contracts remain pinned."
            }, cancellationToken: ct).ConfigureAwait(false);
            return false;
        }
        if (request.Path == "/api/v1/jobs/from-test" && request.Method == "POST")
        {
            if (!await EnsureOperationAllowedAsync(stream, "bulk_transfer", false, ct).ConfigureAwait(false)) return false;
            var body = await ReadAgentBodyAsync<AgentTestRunRequest>(stream, request, ct).ConfigureAwait(false);
            var operation = AgentJobs.PrepareTest(body, ct);
            await WriteAgentJsonAsync(stream, new
            {
                ok = true, id = body.Id, testId = body.TestId, revision = body.Revision,
                state = operation.State, operation = AgentJobStore.Url(body.Id) + "/operation",
                next = AgentJobStore.Url(body.Id) + "?view=summary"
            }, 202, AgentJobStore.Url(body.Id) + "/operation", cancellationToken: ct).ConfigureAwait(false);
            return false;
        }
        const string jobs = "/api/v1/jobs/";
        if (request.Method == "POST" && request.Path.StartsWith(jobs, StringComparison.Ordinal))
        {
            var parts = request.Path[jobs.Length..].Split('/');
            if (parts.Length == 2 && parts[1] == "reuse")
            {
                if (!await EnsureOperationAllowedAsync(stream, "bulk_transfer", false, ct).ConfigureAwait(false)) return false;
                var body = await ReadAgentBodyAsync<AgentReuseRequest>(stream, request, ct).ConfigureAwait(false);
                var id = Uri.UnescapeDataString(parts[0]);
                var operation = AgentJobs.StartReuse(id, body.SourceJobId, ct);
                await WriteAgentJsonAsync(stream, new { ok = true, id, state = operation.State, operation = AgentJobStore.Url(id) + "/operation" },
                    202, AgentJobStore.Url(id) + "/operation", cancellationToken: ct).ConfigureAwait(false);
                return false;
            }
        }
        if (request.Path == "/api/v1/tests" && request.Method == "GET")
        {
            await WriteAgentJsonAsync(stream, AgentJobs.ListTests(
                ReadBoundedQuery(request.Query, "offset", 0, 0, 1000000), ReadBoundedQuery(request.Query, "limit", 10, 1, 100)), cancellationToken: ct).ConfigureAwait(false);
            return false;
        }
        const string tests = "/api/v1/tests/";
        if (!request.Path.StartsWith(tests, StringComparison.Ordinal)) return null;
        var segments = request.Path[tests.Length..].Split('/');
        if (segments.Length == 2 && segments[1] == "bake" && request.Method == "POST")
        {
            if (!await EnsureOperationAllowedAsync(stream, "bulk_transfer", false, ct).ConfigureAwait(false)) return false;
            var body = await ReadAgentBodyAsync<AgentBakeRequest>(stream, request, ct).ConfigureAwait(false);
            using var activity = Activity.TrackTransfer(new { action = "bake", testId = segments[0] });
            var summary = AgentJobs.BakeTest(Uri.UnescapeDataString(segments[0]), body);
            await WriteAgentJsonAsync(stream, summary, location: summary.Self, cancellationToken: ct).ConfigureAwait(false);
            return false;
        }
        if (segments.Length == 2 && request.Method == "GET")
        {
            var full = GetQueryValue(request.Query, "view") == "definition";
            if (full && !await EnsureOperationAllowedAsync(stream, "bulk_transfer", false, ct).ConfigureAwait(false)) return false;
            await WriteAgentJsonAsync(stream, AgentJobs.DescribeTest(Uri.UnescapeDataString(segments[0]), Uri.UnescapeDataString(segments[1]), full), cancellationToken: ct).ConfigureAwait(false);
            return false;
        }
        throw new AgentRequestException(404, "route_not_found", "No matching test-library route.", "GET /api/v1/help?topic=tests for the bake/reference workflow.");
    }
}
