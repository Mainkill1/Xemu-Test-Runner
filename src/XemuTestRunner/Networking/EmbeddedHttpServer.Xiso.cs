using System.Diagnostics;
using System.Text.Json;

namespace XemuTestRunner.Networking;

public sealed partial class EmbeddedHttpServer
{
    private readonly SemaphoreSlim _xisoWaiters = new(32, 32);
    private async Task<bool?> TryXisoRoutesAsync(Stream stream, HttpRequest request, CancellationToken ct)
    {
        if (request.Method == "GET" && request.Path == "/xiso")
        {
            await WriteHtmlAsync(stream, XisoPage.Html, false, ct).ConfigureAwait(false);
            return false;
        }
        if (request.Method == "GET" && request.Path == "/api/v1/help" && GetQueryValue(request.Query, "topic") == "xiso")
        {
            await WriteAgentJsonAsync(stream, new
            {
                capabilities = new[] { "xisoCampaigns", "xisoCategories", "xisoPlanInjection" },
                suites = "/api/v1/xiso-suites", campaigns = "/api/v1/xiso-campaigns", viewer = "/xiso",
                rule = "Creation freezes defaults and selection; only POST /api/v1/xiso-campaigns/{id}/start authorizes execution.",
                selection = "Categories and individual stable IDs form a union. No selection means smoke; qualification/full without selectors means the complete catalog.",
                wait = "GET /api/v1/xiso-campaigns/{id}/wait has automatic heartbeats, no timer options, and never starts a test."
            }, cancellationToken: ct).ConfigureAwait(false);
            return false;
        }
        const string suites = "/api/v1/xiso-suites";
        const string campaigns = "/api/v1/xiso-campaigns";
        if (request.Path == suites && request.Method == "GET")
        {
            await WriteAgentJsonAsync(stream, AgentJobs.ListXisoSuites(ReadBoundedQuery(request.Query, "offset", 0, 0, 1000000),
                ReadBoundedQuery(request.Query, "limit", 10, 1, 100)), cancellationToken: ct).ConfigureAwait(false);
            return false;
        }
        if (request.Path.StartsWith(suites + "/", StringComparison.Ordinal))
        {
            var parts = request.Path[(suites.Length + 1)..].Split('/');
            var id = Uri.UnescapeDataString(parts[0]);
            if (parts.Length == 1 && request.Method == "POST")
            {
                if (!await EnsureOperationAllowedAsync(stream, "bulk_transfer", false, ct).ConfigureAwait(false)) return false;
                var body = await ReadAgentBodyAsync<XisoRegistration>(stream, request, ct).ConfigureAwait(false);
                await WriteAgentJsonAsync(stream, AgentJobs.RegisterXisoSuite(id, body), cancellationToken: ct).ConfigureAwait(false);
                return false;
            }
            if (parts.Length == 2 && parts[1] == "tests" && request.Method == "GET")
            {
                var value = AgentJobs.XisoTests(id, GetQueryValue(request.Query, "category"),
                    ReadBoundedQuery(request.Query, "offset", 0, 0, 1000000), ReadBoundedQuery(request.Query, "limit", 25, 1, 100));
                await WriteAgentJsonAsync(stream, value, cancellationToken: ct).ConfigureAwait(false);
                return false;
            }
        }
        if (request.Path == campaigns)
        {
            if (request.Method == "POST")
            {
                var body = await ReadAgentBodyAsync<XisoCampaignRequest>(stream, request, ct).ConfigureAwait(false);
                var value = AgentJobs.CreateXisoCampaign(body);
                await WriteAgentJsonAsync(stream, AgentJobs.ObserveXisoCampaign(value.Id), cancellationToken: ct).ConfigureAwait(false);
                return false;
            }
            if (request.Method == "GET")
            {
                await WriteAgentJsonAsync(stream, AgentJobs.ListXisoCampaigns(ReadBoundedQuery(request.Query, "offset", 0, 0, 1000000),
                    ReadBoundedQuery(request.Query, "limit", 10, 1, 100)), cancellationToken: ct).ConfigureAwait(false);
                return false;
            }
        }
        if (request.Path.StartsWith(campaigns + "/", StringComparison.Ordinal))
        {
            var parts = request.Path[(campaigns.Length + 1)..].Split('/');
            var id = Uri.UnescapeDataString(parts[0]);
            if (parts.Length == 1 && request.Method == "GET")
            {
                object value = GetQueryValue(request.Query, "view") == "plan" ? AgentJobs.ReadXisoCampaign(id) :
                    AgentJobs.ObserveXisoCampaign(id, ReadBoundedQuery(request.Query, "offset", 0, 0, 1000000), ReadBoundedQuery(request.Query, "limit", 5, 1, 100));
                await WriteAgentJsonAsync(stream, value, cancellationToken: ct).ConfigureAwait(false);
                return false;
            }
            if (parts.Length == 2 && parts[1] == "start" && request.Method == "POST")
            {
                if (request.ContentLength.GetValueOrDefault() > 0)
                {
                    var body = await ReadAgentBodyAsync<JsonElement>(stream, request, ct).ConfigureAwait(false);
                    if (body.ValueKind != JsonValueKind.Object || body.EnumerateObject().Any()) throw new InvalidDataException("Campaign start accepts only an empty body or {}.");
                }
                var value = AgentJobs.StartXisoCampaign(id);
                await WriteAgentJsonAsync(stream, AgentJobs.ObserveXisoCampaign(value.Id), 202, cancellationToken: ct).ConfigureAwait(false);
                return false;
            }
            if (parts.Length == 2 && parts[1] == "wait" && request.Method == "GET")
            {
                if (!string.IsNullOrEmpty(request.Query)) throw new InvalidDataException("Campaign waits have no timer/query options.");
                if (!await _xisoWaiters.WaitAsync(0, ct).ConfigureAwait(false))
                    throw new AgentRequestException(429, "too_many_waiters", "All campaign waiter slots are occupied.", "Retry the same read; do not create another campaign.");
                try
                {
                    var timer = Stopwatch.StartNew();
                    while (true)
                    {
                        var state = AgentJobs.ObserveXisoCampaign(id, limit: 1);
                        var issue = _state.Snapshot().QueueIssue;
                        var blocked = issue is not null && !(issue.Code == "package_stabilizing" && issue.Retryable && !issue.HoldsTesting);
                        var done = state.Terminal == state.Children;
                        var attention = !state.StartRequested || state.Attention > 0 || blocked;
                        if (done || attention || timer.Elapsed >= TimeSpan.FromSeconds(20))
                        {
                            await WriteAgentJsonAsync(stream, new
                            {
                                @event = done ? "finished" : attention ? "attention" : "heartbeat",
                                code = !state.StartRequested ? "start_required" : blocked ? issue!.Code : state.Attention > 0 ? "child_attention" : null,
                                campaign = state, next = "/api/v1/xiso-campaigns/" + id + "/wait"
                            }, cancellationToken: ct).ConfigureAwait(false);
                            break;
                        }
                        await Task.Delay(250, ct).ConfigureAwait(false);
                    }
                }
                finally { _xisoWaiters.Release(); }
                return false;
            }
        }
        return null;
    }
}
