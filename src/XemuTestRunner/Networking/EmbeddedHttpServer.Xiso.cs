using System.Diagnostics;
using System.Text.Json;

namespace XemuTestRunner.Networking;

public sealed partial class EmbeddedHttpServer
{
    private readonly SemaphoreSlim _xisoWaiters = new(32, 32);

    private async Task<bool?> TryXisoRoutesAsync(Stream stream, HttpRequest request, CancellationToken ct)
    {
        if (request.Path == "/api/v1/help" && request.Method == "GET" && GetQueryValue(request.Query, "topic") == "xiso")
        {
            await WriteAgentJsonAsync(stream, new {
                capability = "xisoCampaigns", targets = "/api/v1/xiso-targets", suites = "/api/v1/xiso-suites",
                campaigns = "/api/v1/xiso-campaigns", viewer = "/xiso",
                rule = "Register a matched suite once. Select categories or stable IDs. Omitted settings use frozen suite defaults. Creation never starts tests; explicitly start, then wait without an agent timer.",
                seed = "A clean xiso-seed with a preallocated xemu_perf_tests/xemu_perf_tests_config.json is required."
            }, cancellationToken: ct).ConfigureAwait(false);
            return false;
        }
        if (request.Path == "/api/v1/xiso-targets" && request.Method == "GET")
        {
            await WriteAgentJsonAsync(stream, new { items = AgentJobStore.XisoTargets }, cancellationToken: ct).ConfigureAwait(false);
            return false;
        }
        if (request.Path == "/xiso" && request.Method == "GET")
        {
            await WriteHtmlAsync(stream, XisoPage.Html, false, ct).ConfigureAwait(false);
            return false;
        }
        const string suites = "/api/v1/xiso-suites";
        const string campaigns = "/api/v1/xiso-campaigns";
        if (request.Path == suites)
        {
            if (request.Method == "POST")
            {
                if (!await EnsureOperationAllowedAsync(stream, "bulk_transfer", false, ct).ConfigureAwait(false)) return false;
                var body = await ReadAgentBodyAsync<XisoSuiteRequest>(stream, request, ct).ConfigureAwait(false);
                using var activity = Activity.TrackTransfer(new { action = "registerXisoSuite", body.Id });
                var result = await AgentJobs.RegisterXisoSuiteAsync(body, ct).ConfigureAwait(false);
                await WriteAgentJsonAsync(stream, result, cancellationToken: ct).ConfigureAwait(false); return false;
            }
            if (request.Method == "GET")
            {
                await WriteAgentJsonAsync(stream, AgentJobs.ListXisoSuites(Offset(), Limit()), cancellationToken: ct).ConfigureAwait(false); return false;
            }
        }
        if (request.Path.StartsWith(suites + "/", StringComparison.Ordinal) && request.Method == "GET")
        {
            var parts = request.Path[(suites.Length + 1)..].Split('/');
            var id = Uri.UnescapeDataString(parts[0]);
            object? response = parts.Length == 1 ? AgentJobs.DescribeXisoSuite(id) : parts.Length == 2 && parts[1] == "categories" ? AgentJobs.XisoCategories(id) :
                parts.Length == 2 && parts[1] == "tests" ? AgentJobs.XisoTests(id, GetQueryValue(request.Query, "category"), GetQueryValue(request.Query, "q"), Offset(), Limit()) : null;
            if (response is not null) { await WriteAgentJsonAsync(stream, response, cancellationToken: ct).ConfigureAwait(false); return false; }
        }
        if (request.Path == campaigns)
        {
            if (request.Method == "POST")
            {
                if (!await EnsureOperationAllowedAsync(stream, "bulk_transfer", false, ct).ConfigureAwait(false)) return false;
                var body = await ReadAgentBodyAsync<XisoCampaignRequest>(stream, request, ct).ConfigureAwait(false);
                using var activity = Activity.TrackTransfer(new { action = "createXisoCampaign", body.Id });
                await WriteAgentJsonAsync(stream, AgentJobs.CreateXisoCampaign(body), cancellationToken: ct).ConfigureAwait(false); return false;
            }
            if (request.Method == "GET")
            { await WriteAgentJsonAsync(stream, AgentJobs.ListXisoCampaigns(Offset(), Limit()), cancellationToken: ct).ConfigureAwait(false); return false; }
        }
        if (request.Path.StartsWith(campaigns + "/", StringComparison.Ordinal))
        {
            var parts = request.Path[(campaigns.Length + 1)..].Split('/');
            var id = Uri.UnescapeDataString(parts[0]);
            if (parts.Length == 1 && request.Method == "GET")
            {
                var full = GetQueryValue(request.Query, "view") == "plan";
                if (full && !await EnsureOperationAllowedAsync(stream, "bulk_transfer", false, ct).ConfigureAwait(false)) return false;
                object value = full ? AgentJobs.XisoCampaignPlan(id) : AgentJobs.XisoCampaignStatus(id);
                await WriteAgentJsonAsync(stream, value, cancellationToken: ct).ConfigureAwait(false); return false;
            }
            if (parts.Length == 1 && request.Method == "DELETE")
            { await WriteAgentJsonAsync(stream, AgentJobs.CancelXisoCampaign(id), cancellationToken: ct).ConfigureAwait(false); return false; }
            if (parts.Length == 2 && parts[1] == "start" && request.Method == "POST")
            {
                if (request.ContentLength.GetValueOrDefault() != 0)
                {
                    var body = await ReadAgentBodyAsync<JsonElement>(stream, request, ct).ConfigureAwait(false);
                    if (body.ValueKind != JsonValueKind.Object || body.EnumerateObject().Any()) throw new InvalidDataException("Campaign start takes no body or {}.");
                }
                await WriteAgentJsonAsync(stream, AgentJobs.StartXisoCampaign(id), 202, cancellationToken: ct).ConfigureAwait(false); return false;
            }
            if (parts.Length == 2 && parts[1] == "attempts" && request.Method == "GET")
            { await WriteAgentJsonAsync(stream, AgentJobs.XisoCampaignAttempts(id, Offset(), Limit()), cancellationToken: ct).ConfigureAwait(false); return false; }
            if (parts.Length == 2 && parts[1] == "wait" && request.Method == "GET")
            {
                if (!string.IsNullOrEmpty(request.Query)) throw new InvalidDataException("Campaign wait has no timer/query options. Reconnect to its next URL after a heartbeat.");
                if (!await _xisoWaiters.WaitAsync(0, ct).ConfigureAwait(false)) throw new AgentRequestException(429, "xiso_wait_busy", "Too many campaign waiters.", "Retry the same read; do not create or start another campaign.");
                try
                {
                    var timer = Stopwatch.StartNew();
                    var value = AgentJobs.XisoCampaignStatus(id);
                    while (value.Event == "heartbeat" && timer.Elapsed < TimeSpan.FromSeconds(20))
                    {
                        var state = _state.Snapshot();
                        if (state.QueueIssue is { } issue && (issue.Code != "package_stabilizing" || issue.HoldsTesting || !issue.Retryable))
                        { value = value with { Event = "attention", Error = issue.Code }; break; }
                        await Task.Delay(500, ct).ConfigureAwait(false);
                        value = AgentJobs.XisoCampaignStatus(id);
                    }
                    await WriteAgentJsonAsync(stream, value, cancellationToken: ct).ConfigureAwait(false);
                }
                finally { _xisoWaiters.Release(); }
                return false;
            }
        }
        return null;
        int Offset() => ReadBoundedQuery(request.Query, "offset", 0, 0, 1000000);
        int Limit() => ReadBoundedQuery(request.Query, "limit", 25, 1, 100);
    }
}
