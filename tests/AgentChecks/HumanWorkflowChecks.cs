using System.Net;
using System.Text;
using System.Text.Json;
using XemuTestRunner.Reliability;
using static AgentFixture;

internal static class HumanWorkflowChecks
{
    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("short build references resolve on the server without changing stored hashes", async () =>
        {
            await using var host = new AgentFixture();
            var a = Digest("known"), b = Digest("candidate");
            Seed(host, a, "a", 100); Seed(host, b, "b", 80);
            var result = await host.Json($"/api/v1/compare?A={a[..12].ToUpperInvariant()}&B={b[..12]}");
            Require(result.GetProperty("a").GetString() == a && result.GetProperty("b").GetString() == b, "Short aliases replaced canonical identities.");
            await host.Json("/api/v1/baseline", HttpMethod.Put, new { sha256 = a[..12] });
            var summary = await host.Json("/api/v1/build-results/" + b[..12]);
            Require(summary.GetProperty("sha256").GetString() == b, "Build summary lost the full identity.");
            var page = await host.Json("/api/v1/builds?limit=1");
            Require(page.GetProperty("items").GetArrayLength() == 1 && page.GetProperty("nextOffset").GetInt32() == 1, "Build catalog is not paged.");
            Require(page.GetProperty("items")[0].GetProperty("reference").GetString()!.Length == 12, "Human build reference is not short.");
        }));
        checks.Add(("ambiguous prefixes cannot silently select a baseline", async () =>
        {
            await using var host = new AgentFixture();
            var first = new string('a', 12) + new string('0', 52);
            var second = new string('a', 12) + new string('1', 52);
            Seed(host, first, "first", 100); Seed(host, second, "second", 90);
            var error = await host.Json("/api/v1/baseline", HttpMethod.Put, new { sha256 = new string('a', 12) }, HttpStatusCode.Conflict);
            Require(error.GetProperty("code").GetString() == "reference_ambiguous", "Collision did not produce an actionable conflict.");
            Require(!(await host.Json("/api/v1/baseline")).GetProperty("configured").GetBoolean(), "Ambiguity changed the baseline.");
            var items = (await host.Json("/api/v1/builds")).GetProperty("items");
            Require(items.EnumerateArray().All(item => item.GetProperty("reference").GetString()!.Length > 12), "Catalog offers ambiguous short IDs.");
            await host.Json("/api/v1/baseline", HttpMethod.Put, new { sha256 = first[..13] });
            await host.Json("/api/v1/build-results/123456789abc", expected: HttpStatusCode.NotFound);
            await host.Json("/api/v1/build-results/abcd", expected: HttpStatusCode.BadRequest);
        }));
        checks.Add(("short test revisions normalize before durable request identity is recorded", async () =>
        {
            await using var host = new AgentFixture();
            var revision = await RequestedTestChecks.Setup(host);
            var config = await host.Json("/api/v1/test-configs/smoke/" + revision[..12]);
            Require(config.GetProperty("revision").GetString() == revision, "Config inspection did not resolve the short revision.");
            foreach (var pin in new[] { revision[..12], revision })
            {
                var request = await host.Json("/api/v1/test-runs", HttpMethod.Post,
                    new { id = "human-selection", applicationJobId = "application", testId = "smoke", revision = pin });
                Require(request.GetProperty("revision").GetString() == revision && request.GetProperty("state").GetString() == "uploaded", "Resolution changed identity or authorized execution.");
            }
        }));
        checks.Add(("invalid JSON and missing fields return bounded structured errors without starting", async () =>
        {
            await using var host = new AgentFixture();
            host.Client.Timeout = TimeSpan.FromSeconds(5);
            foreach (var body in new[] { "{", "null", "{}", "{\"id\":null}", "{\"id\":\"a\",\"ID\":\"b\"}" })
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await host.Client.PostAsync("/api/v1/test-runs", content);
                await Error(response, HttpStatusCode.BadRequest);
            }
            Require(!Directory.EnumerateDirectories(host.Paths.Pending).Any(path => !Path.GetFileName(path).StartsWith('.')), "Invalid JSON queued work.");
        }));
        checks.Add(("nested config typos and null collections fail before saving", async () =>
        {
            await using var host = new AgentFixture();
            await RequestedTestChecks.Setup(host);
            foreach (var job in new[] { "{\"executable\":\"xemu.bin\",\"timeuotSeconds\":5}", "{\"executable\":\"xemu.bin\",\"plan\":null}" })
            {
                using var content = new StringContent("{\"sourceJobId\":\"seed\",\"job\":" + job + "}", Encoding.UTF8, "application/json");
                using var response = await host.Client.PostAsync("/api/v1/test-configs/invalid", content);
                var error = await Error(response, HttpStatusCode.BadRequest);
                Require(error.GetProperty("details").GetProperty("field").GetString()!.Length > 0, "Config field error has no field path.");
            }
            Require((await host.Json("/api/v1/test-configs")).GetProperty("items").GetArrayLength() == 1, "Invalid config was published.");
        }));
        checks.Add(("method media type duplicate query and cross-origin errors are explicit", async () =>
        {
            await using var host = new AgentFixture();
            using (var response = await host.Client.PostAsync("/api/v1/compare", new StringContent("{}")))
            {
                await Error(response, HttpStatusCode.MethodNotAllowed);
                Require(response.Content.Headers.Allow.Contains("GET"), "405 omitted the allowed method.");
            }
            using (var response = await host.Client.PostAsync("/api/v1/test-runs", new StringContent("{}")))
                await Error(response, HttpStatusCode.UnsupportedMediaType);
            using (var response = await host.Client.GetAsync("/api/v1/compare?A=aaaaaaaaaaaa&A=bbbbbbbbbbbb&B=cccccccccccc"))
                await Error(response, HttpStatusCode.BadRequest);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/test-runs") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            request.Headers.Add("Origin", "https://unrelated.example");
            using (var response = await host.Client.SendAsync(request)) await Error(response, HttpStatusCode.Forbidden);
        }));
        checks.Add(("long unknown routes do not turn errors into token dumps", async () =>
        {
            await using var host = new AgentFixture();
            using var response = await host.Client.GetAsync("/api/v1/unknown-" + new string('x', 5000));
            await Error(response, HttpStatusCode.NotFound);
        }));
    }

    private static async Task<JsonElement> Error(HttpResponseMessage response, HttpStatusCode expected)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Require(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {Encoding.UTF8.GetString(bytes)}");
        Require(bytes.Length <= 4096, "Error exceeds its response budget.");
        using var doc = JsonDocument.Parse(bytes);
        var error = doc.RootElement;
        Require(!error.GetProperty("ok").GetBoolean() && error.GetProperty("status").GetInt32() == (int)expected, "Error contract is inconsistent.");
        Require(!string.IsNullOrWhiteSpace(error.GetProperty("code").GetString()) && !string.IsNullOrWhiteSpace(error.GetProperty("hint").GetString()), "No error code/recovery hint.");
        Require(response.Headers.GetValues("X-Request-Id").Single() == error.GetProperty("requestId").GetString(), "Correlation identity missing.");
        Require(error.GetProperty("recovery").GetString() is "correct" or "inspect" or "wait", "Missing recovery classification.");
        return error.Clone();
    }

    private static void Seed(AgentFixture host, string sha, string run, double value)
    {
        AtomicJson.Write(Path.Combine(host.Paths.Results, ".build-results", sha, run + ".json"), new
        {
            RunId = run, Sha256 = sha, TestKey = new string('c', 64), EnvironmentKey = new string('d', 64), BuildKey = sha,
            Test = "smoke", Outcome = new { Execution = "completed", Correctness = "passed", Evidence = "complete", Comparison = "eligible" },
            Eligible = true, Issues = Array.Empty<string>(), Metrics = new[] { new { Name = "runtime", Value = value, Unit = "ms", Direction = "lower" } },
            RawCsv = "/api/v1/runs/" + run + "/artifacts/metrics.csv"
        });
    }
}
