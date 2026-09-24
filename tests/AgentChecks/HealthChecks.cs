using System.Net;
using System.Text;
using System.Text.Json;
using static AgentFixture;

internal static class HealthChecks
{
    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("health exposes a compact stable operational contract", async () =>
        {
            await using var host = new AgentFixture();
            var root = await host.Json("/health");
            var versioned = await host.Json("/api/v1/health");

            VerifyCommon(root);
            VerifyCommon(versioned);
            Require(root.GetProperty("instance").GetString() == versioned.GetProperty("instance").GetString(),
                "Health aliases do not describe the same runner instance.");
            Require(root.GetProperty("status").GetString() == "ok", "Idle runner should be healthy.");
            Require(root.GetProperty("ready").GetBoolean(), "Idle runner should be ready.");
            Require(root.GetProperty("acceptingRequests").GetBoolean(), "Health did not report request acceptance.");
            Require(root.GetProperty("phase").GetString() == "idle", "Health did not expose runner phase.");
            Require(root.GetProperty("active").ValueKind == JsonValueKind.Null, "Idle health exposed an active run.");
            Require(root.GetProperty("issue").ValueKind == JsonValueKind.Null, "Idle health exposed a queue issue.");
            Require(root.GetProperty("queue").GetProperty("pending").GetInt32() == 0, "Unexpected pending count.");
            Require(root.GetProperty("queue").GetProperty("testing").GetInt32() == 0, "Unexpected testing count.");
            Require(root.GetProperty("stats").GetProperty("jobsFinished").GetInt32() == 0, "Unexpected completed count.");
            Require(Encoding.UTF8.GetByteCount(root.GetRawText()) <= 2048, "Health response exceeds 2 KiB.");
            Require(!root.TryGetProperty("processId", out _) && !root.TryGetProperty("latestMetric", out _),
                "Health leaked status-detail fields.");

            using var head = new HttpRequestMessage(HttpMethod.Head, "/health");
            using var headResponse = await host.Client.SendAsync(head);
            Require(headResponse.StatusCode == HttpStatusCode.OK, "HEAD /health did not return 200.");
            Require((await headResponse.Content.ReadAsByteArrayAsync()).Length == 0, "HEAD /health returned a body.");
            Require(headResponse.Content.Headers.ContentType?.MediaType == "application/json",
                "HEAD /health did not advertise JSON.");
            Require(headResponse.Headers.CacheControl?.NoStore == true, "Health may be cached.");
        }));

        checks.Add(("health reports active work without becoming unhealthy", async () =>
        {
            await using var host = new AgentFixture();
            host.State.BeginJob("benchmark-job", "run-123", Environment.ProcessId,
                new OperationPolicyDefinition { Mode = "benchmark" });

            var value = await host.Json("/health");
            Require(value.GetProperty("status").GetString() == "ok", "A normal active job degraded health.");
            Require(value.GetProperty("ready").GetBoolean(), "A normal active job made the service unready.");
            Require(value.GetProperty("phase").GetString() == "running", "Active phase missing.");
            var active = value.GetProperty("active");
            Require(active.GetProperty("jobId").GetString() == "benchmark-job", "Active job missing.");
            Require(active.GetProperty("runId").GetString() == "run-123", "Active run missing.");
        }));

        checks.Add(("health reports queue blockage as degraded and retains diagnostics links", async () =>
        {
            await using var host = new AgentFixture();
            host.State.SetQueue(new XemuTestRunner.Queue.QueueSnapshot(3, 1, 9));
            host.State.SetQueueIssue(new XemuTestRunner.Queue.QueueIssue(
                "package_claim_invalid", "held-job", "fixture detail", DateTimeOffset.UtcNow,
                Retryable: false, HoldsTesting: true));

            var value = await host.Json("/health");
            Require(value.GetProperty("status").GetString() == "degraded", "Blocked queue was not degraded.");
            Require(!value.GetProperty("ready").GetBoolean(), "Blocked queue remained ready.");
            var queue = value.GetProperty("queue");
            Require(queue.GetProperty("pending").GetInt32() == 3 &&
                    queue.GetProperty("testing").GetInt32() == 1 &&
                    queue.GetProperty("tested").GetInt32() == 9 &&
                    queue.GetProperty("blocked").GetBoolean(),
                "Queue summary is incomplete.");
            var issue = value.GetProperty("issue");
            Require(issue.GetProperty("code").GetString() == "package_claim_invalid", "Issue code missing.");
            Require(!issue.GetProperty("retryable").GetBoolean() && issue.GetProperty("holdsTesting").GetBoolean(),
                "Issue disposition missing.");
            Require(!issue.TryGetProperty("message", out _), "Health embedded an unbounded queue message.");
            var links = value.GetProperty("links");
            Require(links.GetProperty("status").GetString() == "/api/v1/status" &&
                    links.GetProperty("agent").GetString() == "/api/v1/agent?view=summary",
                "Health did not point to deeper diagnostics.");
        }));
    }

    private static void VerifyCommon(JsonElement value)
    {
        Require(value.GetProperty("schemaVersion").GetInt32() == 1, "Unknown health schema.");
        Require(value.GetProperty("service").GetString() == "xemu-test-runner", "Service identity missing.");
        Require(!string.IsNullOrWhiteSpace(value.GetProperty("version").GetString()), "Build version missing.");
        Require(value.GetProperty("apiVersion").GetString() == "v1", "API version missing.");
        Require(!string.IsNullOrWhiteSpace(value.GetProperty("instance").GetString()), "Instance identity missing.");
        Require(DateTimeOffset.TryParse(value.GetProperty("timestampUtc").GetString(), out _), "Timestamp invalid.");
        Require(DateTimeOffset.TryParse(value.GetProperty("startedUtc").GetString(), out _), "Start time invalid.");
        Require(value.GetProperty("uptimeMs").GetInt64() >= 0, "Uptime invalid.");
        Require(value.GetProperty("platform").GetProperty("os").GetString() is { Length: > 0 }, "OS identity missing.");
        Require(value.GetProperty("platform").GetProperty("architecture").GetString() is { Length: > 0 },
            "Architecture identity missing.");
    }
}
