using System.Net;
using System.Text;
using System.Text.Json;
using static AgentFixture;

var failures = 0;
var total = 0;
async Task Check(string name, Func<Task> run)
{
    total++;
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + e); }
}

await Check("XISO workflow is discoverable without starting a test", async () =>
{
    await using var host = new AgentFixture();
    var help = await host.Json("/api/v1/help?topic=xiso");
    Require(help.GetProperty("capabilities").EnumerateArray().Any(x => x.GetString() == "xisoCampaigns"), "Missing capability.");
    var suites = await host.Json("/api/v1/xiso-suites");
    Require(suites.GetProperty("items").GetArrayLength() == 0, "New runner chose a suite implicitly.");
});
await Check("registering a pinned suite exposes categories and individual tests", async () =>
{
    await using var host = new AgentFixture();
    await Setup(host);
    var suites = await host.Json("/api/v1/xiso-suites");
    Require(suites.GetProperty("items")[0].GetProperty("leafCount").GetInt32() == 4, "Wrong suite leaf count.");
    var tests = await host.Json("/api/v1/xiso-suites/pilot/tests?category=shaders");
    Require(tests.GetProperty("items").GetArrayLength() == 2, "Shader category selection is incomplete.");
    Require(tests.GetProperty("items")[0].GetProperty("id").GetString() == "shader.train", "Stable ID was not retained.");
});
await Check("small category request freezes defaults and never starts automatically", async () =>
{
    await using var host = new AgentFixture();
    await Setup(host);
    var body = new { id = "category", application = "application", categories = new[] { "shaders" } };
    var value = await host.Json("/api/v1/xiso-campaigns", HttpMethod.Post, body);
    Require(value.GetProperty("state").GetString() == "created", "Campaign creation authorized execution.");
    Require(value.GetProperty("selectedLeaves").GetInt32() == 2, "Category did not resolve its leaves.");
    var detail = await host.Json("/api/v1/xiso-campaigns/category?view=plan");
    Require(detail.GetProperty("plan").GetProperty("settings").GetProperty("measurement_iterations_multiplier").GetInt32() == 1, "Default fixed work missing.");
    var same = await host.Json("/api/v1/xiso-campaigns", HttpMethod.Post, body);
    Require(same.GetProperty("revision").GetString() == value.GetProperty("revision").GetString(), "Retry changed the plan.");
    await host.Json("/api/v1/xiso-campaigns", HttpMethod.Post,
        new { id = "category", application = "application", tests = new[] { "cpu.math" } }, HttpStatusCode.Conflict);
    Require(!Directory.EnumerateDirectories(host.Paths.Pending).Any(p => !Path.GetFileName(p).StartsWith('.')), "Upload-only campaign reached the execution queue.");
});
await Check("individual selection expands required checkpoints but rejects unknown selectors", async () =>
{
    await using var host = new AgentFixture();
    await Setup(host);
    var value = await host.Json("/api/v1/xiso-campaigns", HttpMethod.Post,
        new { id = "leaf", application = "application", tests = new[] { "shader.train" } });
    Require(value.GetProperty("selectedLeaves").GetInt32() == 2, "Atomic checkpoint closure was not expanded.");
    await host.Json("/api/v1/xiso-campaigns", HttpMethod.Post,
        new { id = "bad", application = "application", categories = new[] { "shdaers" } }, HttpStatusCode.BadRequest);
    await host.Json("/api/v1/xiso-campaigns", HttpMethod.Post,
        new { id = "bad", application = "application", tests = new[] { "missing.test" } }, HttpStatusCode.BadRequest);
    await host.Json("/api/v1/xiso-campaigns", HttpMethod.Post,
        new { id = "bad", application = "application", settings = new { guessedSetting = 1 } }, HttpStatusCode.BadRequest);
});
await Check("default smoke and explicit ABBA are fully inspectable before start", async () =>
{
    await using var host = new AgentFixture();
    await Setup(host);
    var smoke = await host.Json("/api/v1/xiso-campaigns", HttpMethod.Post,
        new { id = "smoke", application = "application" });
    Require(smoke.GetProperty("selectedLeaves").GetInt32() == 1, "Missing selection expanded to the whole suite instead of smoke.");
    var abba = await host.Json("/api/v1/xiso-campaigns", HttpMethod.Post,
        new { id = "abba", application = "application", reference = "seed", tests = new[] { "cpu.math" } });
    Require(abba.GetProperty("children").GetInt32() == 4, "ABBA schedule was not frozen.");
    var wait = await host.Json("/api/v1/xiso-campaigns/abba/wait");
    Require(wait.GetProperty("event").GetString() == "attention" && wait.GetProperty("code").GetString() == "start_required", "Waiting started unrequested tests.");
});
await Check("explicit start persists once and queues behind current work", async () =>
{
    await using var host = new AgentFixture();
    await Setup(host);
    host.State.BeginJob("busy", "busy-run", Environment.ProcessId);
    await host.Json("/api/v1/xiso-campaigns", HttpMethod.Post,
        new { id = "requested", application = "application", tests = new[] { "cpu.math" } });
    foreach (var ignored in Enumerable.Range(0, 2))
        await host.Json("/api/v1/xiso-campaigns/requested/start", HttpMethod.Post, new { }, HttpStatusCode.Accepted);
    var value = await host.Json("/api/v1/xiso-campaigns/requested");
    Require(value.GetProperty("startRequested").GetBoolean(), "Start intent was not durable.");
    Require(value.GetProperty("children").GetInt32() == 1, "Repeated start duplicated a child.");
    Require(!Directory.EnumerateDirectories(host.Paths.Pending).Any(p => !Path.GetFileName(p).StartsWith('.')), "Preparation disturbed the current test.");
});
Console.WriteLine($"XISO campaign checks: {total - failures}/{total} passed.");
return failures == 0 ? 0 : 1;

static async Task Setup(AgentFixture host)
{
    host.State.SetPhase("fixture");
    foreach (var id in new[] { "seed", "application" })
    {
        var job = new
        {
            id, executable = "xemu.bin", arguments = new[] { "-config_path", "xemu.toml" }, timeoutSeconds = 60,
            runtimeState = new
            {
                enabled = true,
                diskAssets = new[] { new { assetId = "empty", expectedSha256 = Digest("blank"), destination = "hdd.qcow2" } },
                isolation = new { requirePrivateGuestState = false, allowUncontrolledDriverCache = true,
                    readOnlyAssets = new[] { new { field = "dvd_path", assetId = "iso", expectedSha256 = Digest("iso") } } }
            },
            workload = new { guestHddResults = new { image = "hdd.qcow2", partitionOffsetBytes = 0L,
                partitionLengthBytes = 4L * 1024 * 1024, guestPath = "xemu_perf_tests/results.txt",
                expectedResults = "reference.json", expectedResultsSha256 = Digest("[]") } }
        };
        var files = new Dictionary<string, string> { ["xemu.bin"] = "fixture", ["xemu.toml"] = "[sys.files]\nhdd_path=\"{runtimeDir}/hdd.qcow2\"\n", ["reference.json"] = "[]" };
        await host.Json("/api/v1/jobs", HttpMethod.Post, new { id, job,
            files = files.Select(f => new { path = f.Key, length = Encoding.UTF8.GetByteCount(f.Value), sha256 = Digest(f.Value), executable = f.Key == "xemu.bin" }).ToArray() });
        foreach (var f in files)
        {
            using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(f.Value));
            using var response = await host.Client.PutAsync($"/api/v1/jobs/{id}/files/{f.Key}", content);
            Require(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        }
    }
    foreach (var pair in new[] { (Id: "iso", Data: "iso", Kind: "readonly-input"), (Id: "empty", Data: "blank", Kind: "xiso-seed") })
    {
        await host.Json("/api/v1/disk-assets", HttpMethod.Post,
            new { id = pair.Id, kind = pair.Kind, length = pair.Data.Length, sha256 = Digest(pair.Data) });
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(pair.Data));
        using var response = await host.Client.PutAsync($"/api/v1/disk-assets/{pair.Id}/content", content);
        Require(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }
    var baked = await host.Json("/api/v1/tests/base/bake", HttpMethod.Post, new { sourceJobId = "seed" });
    var definitions = new[]
    {
        new { id = "cpu.math", kind = "leaf", suite_id = "cpu", revision = 1, display_name = "CPU math", supported_targets = new[] { "xemu" }, isolation = "same_process", timeout_ms = 30000, execution = new { legacy_suite = "CPU", legacy_test = "Math" } },
        new { id = "shader.train", kind = "leaf", suite_id = "shaders", revision = 1, display_name = "Shader train", supported_targets = new[] { "xemu" }, isolation = "same_process", timeout_ms = 30000, execution = new { legacy_suite = "Shader", legacy_test = "Lifecycle" } },
        new { id = "shader.measure", kind = "leaf", suite_id = "shaders", revision = 1, display_name = "Shader measure", supported_targets = new[] { "xemu" }, isolation = "same_process", timeout_ms = 30000, execution = new { legacy_suite = "Shader", legacy_test = "Lifecycle" } },
        new { id = "surface.read", kind = "leaf", suite_id = "surfaces", revision = 1, display_name = "Readback", supported_targets = new[] { "xemu" }, isolation = "same_process", timeout_ms = 30000, execution = new { legacy_suite = "Surface", legacy_test = "Read" } }
    };
    var catalogId = "sha256:" + Digest("catalog-descriptors");
    var catalog = JsonSerializer.Serialize(new { schema_version = 1, catalog_id = catalogId, leaf_count = 4, group_count = 0, tests = definitions });
    var manifest = new { schemaVersion = 1, sourceCommit = new string('a', 40), qualification = "candidate", isoSha256 = Digest("iso"), catalogSha256 = Digest(catalog), catalogId,
        categories = new[] { new { id = "cpu", name = "CPU", tests = new[] { "cpu.math" } }, new { id = "shaders", name = "Shaders", tests = new[] { "shader.train", "shader.measure" } }, new { id = "surfaces", name = "Surfaces", tests = new[] { "surface.read" } } },
        atomicGroups = new[] { new[] { "shader.train", "shader.measure" } }, smoke = new[] { "cpu.math" } };
    await host.Json("/api/v1/xiso-suites/pilot", HttpMethod.Post,
        new { manifest, catalogJson = catalog, template = new { id = "base", revision = baked.GetProperty("revision").GetString() }, isoAssetId = "iso" });
}
