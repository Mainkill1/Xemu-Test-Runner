using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Runtime;
using static AgentFixture;

var failures = 0;
var count = 0;
async Task Check(string name, Func<Task> run)
{
    count++;
    try { await run(); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + error); }
}

await Check("known shader target is pinned and explicitly candidate", async () =>
{
    await using var host = new AgentFixture();
    var targets = await host.Json("/api/v1/xiso-targets");
    var target = targets.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("id").GetString() == "shader-pilot-51bc23d");
    Require(target.GetProperty("isoSha256").GetString() == "b944d317035779afb15b7f7a0d90b0e35dd93b2257addba78c073438979cbf14", "Wrong shader target.");
    Require(target.GetProperty("qualification").GetString() == "candidate", "Unqualified shader pilot became known-good.");
});
await Check("registration reads the catalog inside the immutable ISO", async () =>
{
    await using var host = new AgentFixture();
    var suite = await Setup(host);
    Require(suite.GetProperty("leafCount").GetInt32() == 9, "Catalog count was guessed.");
    var categories = await host.Json("/api/v1/xiso-suites/fixture/categories");
    Require(categories.GetProperty("items").EnumerateArray().Any(x => x.GetProperty("id").GetString() == "shaders"), "Shader category missing.");
    var tests = await host.Json("/api/v1/xiso-suites/fixture/tests?category=shaders");
    Require(tests.GetProperty("items").GetArrayLength() == 2, "Category did not filter shader leaves.");
    Require(!Directory.EnumerateDirectories(host.Paths.Pending).Any(x => !Path.GetFileName(x).StartsWith('.')), "Registration queued executable work.");
});
await Check("category selection is small upload-only and freezes defaults", async () =>
{
    await using var host = new AgentFixture();
    await Setup(host);
    var value = await host.Json("/api/v1/xiso-campaigns", HttpMethod.Post, new { id = "shader-run", application = "application", categories = new[] { "shaders" } });
    Require(!value.GetProperty("startRequested").GetBoolean(), "Selection started tests.");
    Require(value.GetProperty("selectedLeaves").GetInt32() == 2, "Wrong selected leaf count.");
    Require(value.GetProperty("chunkCount").GetInt32() == 2, "Shader pilot cases were mixed into one launch.");
    var plan = await host.Json("/api/v1/xiso-campaigns/shader-run?view=plan");
    Require(plan.GetProperty("settings").GetProperty("measurement_iterations_multiplier").GetInt32() == 1, "Missing settings were not resolved.");
    var again = await host.Json("/api/v1/xiso-campaigns", HttpMethod.Post, new { id = "shader-run", application = "application", categories = new[] { "shaders" } });
    Require(again.GetProperty("revision").GetString() == value.GetProperty("revision").GetString(), "Idempotent creation drifted.");
    await host.Json("/api/v1/xiso-campaigns", HttpMethod.Post, new { id = "shader-run", application = "application", tests = new[] { "cpu.direct" } }, HttpStatusCode.Conflict);
});
await Check("individual selection expands only inseparable memory checkpoints", async () =>
{
    await using var host = new AgentFixture();
    await Setup(host);
    var value = await host.Json("/api/v1/xiso-campaigns", HttpMethod.Post, new { id = "memory-run", application = "application", tests = new[] { "surface.vulkan_memory_pressure.representative.growth" } });
    Require(value.GetProperty("selectedLeaves").GetInt32() == 5, "Atomic checkpoint dependency was not closed.");
    var plan = await host.Json("/api/v1/xiso-campaigns/memory-run?view=plan");
    Require(plan.GetProperty("addedDependencies").GetArrayLength() == 4, "Automatic dependency expansion was not disclosed.");
});
await Check("unknown selectors and settings fail before campaign publication", async () =>
{
    await using var host = new AgentFixture();
    await Setup(host);
    await host.Json("/api/v1/xiso-campaigns", HttpMethod.Post, new { id = "bad", application = "application", categories = new[] { "shadres" } }, HttpStatusCode.BadRequest);
    await host.Json("/api/v1/xiso-campaigns", HttpMethod.Post, new { id = "bad", application = "application", tests = new[] { "not-a-test" } }, HttpStatusCode.BadRequest);
    await host.Json("/api/v1/xiso-campaigns", HttpMethod.Post, new { id = "bad", application = "application", settings = new { measurement_iterations_multiplier = 0 } }, HttpStatusCode.BadRequest);
    await host.Json("/api/v1/xiso-campaigns", HttpMethod.Post, new { id = "bad", application = "application", settings = new { guessing = true } }, HttpStatusCode.BadRequest);
    await host.Json("/api/v1/xiso-campaigns/bad", expected: HttpStatusCode.NotFound);
});
await Check("explicit start is durable and cancellation does not create work", async () =>
{
    await using var host = new AgentFixture();
    await Setup(host);
    await host.Json("/api/v1/xiso-campaigns", HttpMethod.Post, new { id = "queue-me", application = "application", tests = new[] { "cpu.direct" } });
    host.State.BeginJob("active", "active-run", Environment.ProcessId, new OperationPolicyDefinition { Mode = "benchmark" });
    var a = await host.Json("/api/v1/xiso-campaigns/queue-me/start", HttpMethod.Post, new { }, HttpStatusCode.Accepted);
    var b = await host.Json("/api/v1/xiso-campaigns/queue-me/start", HttpMethod.Post, new { }, HttpStatusCode.Accepted);
    Require(a.GetProperty("revision").GetString() == b.GetProperty("revision").GetString(), "Start changed campaign identity.");
    await host.Json("/api/v1/xiso-campaigns/queue-me", HttpMethod.Delete);
    var cancelled = await host.Json("/api/v1/xiso-campaigns/queue-me");
    Require(cancelled.GetProperty("state").GetString() == "cancelled", "Pending campaign did not cancel.");
    Require(!Directory.EnumerateDirectories(host.Paths.Pending).Any(x => !Path.GetFileName(x).StartsWith('.')), "Active benchmark was disturbed.");
});
await Check("private plan injection does not change the seed or grow the image", async () =>
{
    await using var host = new AgentFixture();
    await Setup(host);
    await host.Json("/api/v1/xiso-campaigns", HttpMethod.Post, new { id = "inject", application = "application", tests = new[] { "cpu.direct" } });
    var plan = await host.Json("/api/v1/xiso-campaigns/inject?view=plan");
    var runtimeJson = plan.GetProperty("chunks")[0].GetProperty("runtimeState").GetRawText();
    var definition = JsonSerializer.Deserialize<RuntimeStateDefinition>(runtimeJson, ConfigLoader.JsonOptions)!;
    var seedPath = Path.Combine(host.Draft("seed"), "prepared.raw");
    var before = SHA256.HashData(await File.ReadAllBytesAsync(seedPath));
    var materialized = await RuntimeStateManager.MaterializeAsync(definition, host.Draft("seed"), host.Paths.Workspace, "inject-run", CancellationToken.None);
    Require(materialized is not null, "No private disk.");
    var disk = await File.ReadAllBytesAsync(Path.Combine(materialized!.Directory, "hdd.qcow2"));
    Require(disk.Length == Seed().Length, "Plan injection grew the disk.");
    var config = Encoding.UTF8.GetString(disk, 16384, 65536).TrimEnd(' ');
    using var actual = JsonDocument.Parse(config);
    Require(actual.RootElement.GetProperty("resolved_plan").GetProperty("selected_leaf_count").GetInt32() == 1, "Resolved plan was not injected.");
    var after = SHA256.HashData(await File.ReadAllBytesAsync(seedPath));
    Require(before.SequenceEqual(after), "Shared seed was edited.");
});
Console.WriteLine($"XISO checks: {count - failures}/{count} passed.");
return failures == 0 ? 0 : 1;

static byte[] Seed()
{
    var bytes = new byte[2 * 1024 * 1024];
    "FATX"u8.CopyTo(bytes); Put32(bytes, 8, 8); Put32(bytes, 12, 1);
    void Fat(int c, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4096 + c * 2), v);
    Fat(0, 0xffff); Fat(1, 0xffff); Fat(2, 0xffff);
    for (var c = 3; c < 19; c++) Fat(c, c == 18 ? (ushort)0xffff : (ushort)(c + 1));
    bytes.AsSpan(8192, 8192).Fill(0xff);
    Entry(bytes, 8192, "xemu_perf_tests", 2, 0, true);
    Entry(bytes, 12288, "xemu_perf_tests_config.json", 3, 65536, false);
    bytes.AsSpan(16384, 65536).Fill((byte)' '); "{}"u8.CopyTo(bytes.AsSpan(16384));
    return bytes;
}
static void Entry(byte[] b, int off, string name, uint cluster, uint length, bool directory)
{
    b.AsSpan(off, 64).Clear(); b[off] = (byte)name.Length; b[off + 1] = directory ? (byte)0x10 : (byte)0;
    Encoding.ASCII.GetBytes(name).CopyTo(b, off + 2); Put32(b, off + 44, cluster); Put32(b, off + 48, length);
}
static void Put32(byte[] b, int off, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(off), value);
static byte[] Iso(byte[] catalog)
{
    var bytes = new byte[((34 * 2048 + catalog.Length + 2047) / 2048) * 2048];
    Encoding.ASCII.GetBytes("MICROSOFT*XBOX*MEDIA").CopyTo(bytes, 32 * 2048);
    Encoding.ASCII.GetBytes("MICROSOFT*XBOX*MEDIA").CopyTo(bytes, 33 * 2048 - 20);
    Put32(bytes, 32 * 2048 + 20, 33); Put32(bytes, 32 * 2048 + 24, 26);
    Put32(bytes, 33 * 2048 + 4, 34); Put32(bytes, 33 * 2048 + 8, (uint)catalog.Length);
    bytes[33 * 2048 + 13] = 12; Encoding.ASCII.GetBytes("catalog.json").CopyTo(bytes, 33 * 2048 + 14);
    catalog.CopyTo(bytes, 34 * 2048); return bytes;
}
static async Task<JsonElement> Setup(AgentFixture host)
{
    host.State.SetPhase("fixture");
    var ids = new[] { "cpu.direct", "surface.read", "shader_lifecycle.pipeline_train", "shader_lifecycle.pipeline_uniform_only" }
        .Concat(new[] { "growth", "plateau", "alias_resize", "reuse", "idle_retention" }.Select(x => "surface.vulkan_memory_pressure.representative." + x)).ToArray();
    var leaves = ids.Select(id => new {
        id, revision = 1, kind = "leaf", suite_id = id.StartsWith("cpu") ? "cpu_translation_blocks" : id.StartsWith("shader") ? "shader_lifecycle" : "surface_rendering",
        display_name = id, description = "fixture", tags = new[] { "performance" }, supported_targets = new[] { "xemu" }, isolation = "same_process", timeout_ms = 30000,
        execution = new { legacy_suite = id.StartsWith("shader") ? "ShaderLifecycle" : "Fixture", legacy_test = id.Contains("memory_pressure") ? "MemoryPressure" : id }
    }).ToArray();
    var catalog = JsonSerializer.SerializeToUtf8Bytes(new { schema_version = 1, catalog_id = "sha256:" + new string('c', 64), leaf_count = ids.Length, group_count = 0, tests = leaves });
    var reference = JsonSerializer.SerializeToUtf8Bytes(ids.Select(id => new { schema_version = 1, id, kind = "leaf", revision = 1, outcome = "PASS", iterations = 1, sample_count = 1, measurement_iterations_multiplier = 1, warmup_iterations = 0, gpu_completion_mode = "per_iteration", framebuffer_fnv1a64 = "abc123", raw_results = new[] { 1 } }));
    var payload = new Dictionary<string, byte[]> { ["xemu.bin"] = "fixture"u8.ToArray(), ["suite.iso"] = Iso(catalog), ["prepared.raw"] = Seed(), ["reference.json"] = reference, ["xemu.toml"] = "[system.files]\nhdd_path='{runtimeDir}/hdd.qcow2'\n"u8.ToArray() };
    var declarations = payload.Select(pair => new { path = pair.Key, length = pair.Value.LongLength, sha256 = Sha(pair.Value), executable = pair.Key == "xemu.bin" }).ToArray();
    await host.Json("/api/v1/jobs", HttpMethod.Post, new {
        id = "seed", job = new { id = "seed", executable = "xemu.bin", timeoutSeconds = 10, arguments = new[] { "-config_path", "xemu.toml" }, runtimeState = new { enabled = true, files = new[] { new { source = "prepared.raw", destination = "hdd.qcow2", expectedSha256 = Sha(payload["prepared.raw"]) } }, isolation = new { requirePrivateGuestState = false } }, workload = new { guestHddResults = new { image = "hdd.qcow2", partitionOffsetBytes = 0, partitionLengthBytes = payload["prepared.raw"].Length, expectedResults = "reference.json", expectedResultsSha256 = Sha(reference) } } }, files = declarations });
    foreach (var (path, bytes) in payload) { using var body = new ByteArrayContent(bytes); using var response = await host.Client.PutAsync("/api/v1/jobs/seed/files/" + path, body); Require(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync()); }
    var baked = await host.Json("/api/v1/tests/base/bake", HttpMethod.Post, new { sourceJobId = "seed" });
    await host.Json("/api/v1/jobs", HttpMethod.Post, new { id = "application", job = new { id = "application", executable = "xemu.bin" }, files = declarations.Where(x => x.path == "xemu.bin").ToArray() });
    using (var body = new ByteArrayContent("fixture"u8.ToArray())) using (var response = await host.Client.PutAsync("/api/v1/jobs/application/files/xemu.bin", body)) Require(response.StatusCode == HttpStatusCode.Created, "Application upload failed.");
    return await host.Json("/api/v1/xiso-suites", HttpMethod.Post, new { id = "fixture", testId = "base", revision = baked.GetProperty("revision").GetString() });
}
static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
