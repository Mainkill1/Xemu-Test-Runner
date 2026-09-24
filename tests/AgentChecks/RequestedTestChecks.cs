using System.Net;
using System.Text;
using System.Text.Json;
using XemuTestRunner.Reliability;
using static AgentFixture;

internal static class RequestedTestChecks
{
    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("named configs can be uploaded listed and inspected without execution", async () =>
        {
            await using var host = new AgentFixture();
            var revision = await Setup(host);
            var catalog = await host.Json("/api/v1/test-configs");
            Require(catalog.GetProperty("items").GetArrayLength() == 1, "Config is not discoverable.");
            var config = await host.Json("/api/v1/test-configs/smoke/" + revision);
            Require(config.GetProperty("definition").GetProperty("job").GetProperty("plan").GetArrayLength() == 2, "Full config was not exposed.");
            Require(!Directory.EnumerateDirectories(host.Paths.Pending).Any(p => !Path.GetFileName(p).StartsWith('.')), "Config upload queued a test.");
            using var page = await host.Client.GetAsync("/tests");
            Require(page.IsSuccessStatusCode && (await page.Content.ReadAsStringAsync()).Contains("Test configurations"), "Missing test config viewer.");
        }));
        checks.Add(("config renaming creates a separate catalog name without modifying source", async () =>
        {
            await using var host = new AgentFixture();
            await Setup(host);
            var other = await SaveConfig(host, "diagnostic", 30);
            var definition = await host.Json("/api/v1/test-configs/diagnostic/" + other);
            Require(definition.GetProperty("definition").GetProperty("job").GetProperty("timeoutSeconds").GetInt32() == 30, "New config ignored uploaded values.");
            var source = await host.Json("/api/v1/jobs/seed");
            Require(source.GetProperty("job").GetProperty("timeoutSeconds").GetInt32() == 10, "Uploading config rewrote source job.");
            Require((await host.Json("/api/v1/test-configs")).GetProperty("items").GetArrayLength() == 2, "New name replaced old name.");
        }));
        checks.Add(("uploading a test request never starts it", async () =>
        {
            await using var host = new AgentFixture();
            var revision = await Setup(host);
            var created = await Create(host, "requested", revision);
            Require(created.GetProperty("state").GetString() == "uploaded", "Request was not upload-only.");
            await Task.Delay(600);
            var status = await host.Json("/api/v1/test-runs/requested");
            Require(status.GetProperty("state").GetString() == "uploaded", "A background loop started an unrequested test.");
            Require(!Directory.Exists(Path.Combine(host.Paths.Pending, "agent-requested")), "Unrequested test reached execution queue.");
        }));
        checks.Add(("explicit start queues behind an active benchmark and reuses the application", async () =>
        {
            await using var host = new AgentFixture();
            var revision = await Setup(host);
            await Create(host, "requested", revision);
            host.State.BeginJob("active", "active-run", Environment.ProcessId, new OperationPolicyDefinition { Mode = "benchmark" });
            var started = await host.Json("/api/v1/test-runs/requested/start", HttpMethod.Post, new { }, HttpStatusCode.Accepted);
            Require(started.GetProperty("state").GetString() == "queued", "Explicit request was not acknowledged as queued.");
            await Task.Delay(600);
            Require(!Directory.Exists(host.Draft("requested")), "Heavy preparation disturbed the active benchmark.");
            host.State.EndJob("completed");
            await Until(host, "requested", "queuedForExecution");
            Require(await host.Client.GetStringAsync("/api/v1/jobs/requested/files/xemu.bin") == "candidate", "Application bytes were not reused.");
            Require(await host.Client.GetStringAsync("/api/v1/jobs/requested/files/workload.bin") == "workload", "Fixed workload was not reused.");
            Require(await host.Client.GetStringAsync("/api/v1/jobs/seed/files/xemu.bin") == "fixture", "Source was modified.");
        }));
        checks.Add(("start is idempotent and queued tests are submitted serially", async () =>
        {
            await using var host = new AgentFixture();
            var revision = await Setup(host);
            host.State.BeginJob("active", "active-run", Environment.ProcessId);
            await Create(host, "first", revision);
            await Create(host, "second", revision);
            await host.Json("/api/v1/test-runs/first/start", HttpMethod.Post, new { }, HttpStatusCode.Accepted);
            await host.Json("/api/v1/test-runs/first/start", HttpMethod.Post, new { }, HttpStatusCode.Accepted);
            await host.Json("/api/v1/test-runs/second/start", HttpMethod.Post, new { }, HttpStatusCode.Accepted);
            host.State.EndJob("completed");
            await Until(host, "first", "queuedForExecution");
            Require(!Directory.Exists(Path.Combine(host.Paths.Pending, "agent-second")), "Next test was prepared before the earlier queue entry finished.");
            Directory.Move(Path.Combine(host.Paths.Pending, "agent-first"), Path.Combine(host.Paths.Tested, "agent-first"));
            await Until(host, "second", "queuedForExecution");
            Require((await host.Json("/api/v1/test-runs/first")).GetProperty("state").GetString() == "tested", "Completed request did not reconcile.");
        }));
        checks.Add(("unrequested work can be cancelled and conflicting identities are rejected", async () =>
        {
            await using var host = new AgentFixture();
            var revision = await Setup(host);
            await Create(host, "cancel-me", revision);
            await host.Json("/api/v1/test-runs/cancel-me", HttpMethod.Delete);
            await host.Json("/api/v1/test-runs/cancel-me/start", HttpMethod.Post, new { }, HttpStatusCode.Conflict);
            await host.Json("/api/v1/test-runs", HttpMethod.Post, new { id = "cancel-me", applicationJobId = "seed", testId = "smoke", revision }, HttpStatusCode.Conflict);
            Require((await host.Json("/api/v1/test-runs/cancel-me")).GetProperty("state").GetString() == "cancelled", "Cancelled work restarted.");
        }));
        checks.Add(("bad config references and unexpected start fields cannot launch", async () =>
        {
            await using var host = new AgentFixture();
            var revision = await Setup(host);
            await host.Json("/api/v1/test-runs", HttpMethod.Post, new { id = "bad", applicationJobId = "application", testId = "smoke", revision = new string('0', 64) }, HttpStatusCode.NotFound);
            await Create(host, "good", revision);
            await host.Json("/api/v1/test-runs/good/start", HttpMethod.Post, new { startOther = true }, HttpStatusCode.BadRequest);
            Require((await host.Json("/api/v1/test-runs/good")).GetProperty("state").GetString() == "uploaded", "Rejected start mutated the request.");
        }));
    }

    internal static async Task<string> Setup(AgentFixture host)
    {
        await host.Json("/api/v1/jobs", HttpMethod.Post, new
        {
            id = "seed", job = new { id = "seed", executable = "xemu.bin", timeoutSeconds = 10 },
            files = new[] {
                new { path = "xemu.bin", length = 7, sha256 = Digest("fixture"), executable = true },
                new { path = "workload.bin", length = 8, sha256 = Digest("workload"), executable = false } }
        });
        await Upload(host, "seed", "xemu.bin", "fixture");
        await Upload(host, "seed", "workload.bin", "workload");
        await host.Json("/api/v1/jobs", HttpMethod.Post, new
        {
            id = "application", job = new { id = "application", executable = "renamed.bin", timeoutSeconds = 10 },
            files = new[] { new { path = "renamed.bin", length = 9, sha256 = Digest("candidate"), executable = true } }
        });
        await Upload(host, "application", "renamed.bin", "candidate");
        return await SaveConfig(host, "smoke", 10);
    }

    private static async Task<string> SaveConfig(AgentFixture host, string name, int timeout)
    {
        var saved = await host.Json("/api/v1/test-configs/" + name, HttpMethod.Post, new
        {
            sourceJobId = "seed", description = "Inspect me", job = new {
                executable = "xemu.bin", requiredFiles = new[] { "workload.bin" }, timeoutSeconds = timeout,
                plan = new[] { new { type = "wait", delayMs = 1 }, new { type = "wait", delayMs = 2 } } }
        });
        return saved.GetProperty("revision").GetString()!;
    }

    private static Task<JsonElement> Create(AgentFixture host, string id, string revision) => host.Json("/api/v1/test-runs", HttpMethod.Post,
        new { id, applicationJobId = "application", testId = "smoke", revision });

    private static async Task Upload(AgentFixture host, string id, string file, string data)
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(data));
        using var response = await host.Client.PutAsync($"/api/v1/jobs/{id}/files/{file}", content);
        Require(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    internal static async Task Until(AgentFixture host, string id, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var current = await host.Json("/api/v1/test-runs/" + id);
            var state = current.GetProperty("state").GetString();
            if (state == expected) return;
            Require(state is not ("failed" or "held"), current.GetRawText());
            await Task.Delay(100);
        }
        throw new TimeoutException("Requested test did not reach " + expected);
    }
}
