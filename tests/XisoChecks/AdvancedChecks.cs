using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Reliability;
using XemuTestRunner.Runtime;
using static AgentFixture;

internal static class AdvancedChecks
{
    public static async Task Run(Func<string, Func<Task>, Task> check, Func<AgentFixture, Task<JsonElement>> setup, Func<byte[]> seed)
    {
        await check("self-contained QCOW2 plan injection preserves valid cluster mappings", async () =>
        {
            await using var host = new AgentFixture(); await setup(host);
            var definition = await Definition(host, "qcow");
            var bytes = Qcow(seed()); var original = DigestBytes(bytes);
            SetDisk(host, definition, "qcow-seed", bytes);
            var runtime = await RuntimeStateManager.MaterializeAsync(definition, host.Draft("seed"), host.Paths.Workspace, "qcow-run", CancellationToken.None);
            using var disk = GuestDiskReader.Open(Path.Combine(runtime!.Directory, "hdd.qcow2"), CancellationToken.None);
            var json = new FatxResultReader(disk, 0, seed().Length).ReadFile(FatxResultReader.PlanPath, 1024 * 1024)!;
            using var parsed = JsonDocument.Parse(json);
            Require(parsed.RootElement.GetProperty("resolved_plan").GetProperty("plan_id").GetString() == definition.Xiso!.PlanId, "QCOW2 plan bytes were not updated.");
            Require(DigestBytes(File.ReadAllBytes(new DiskAssetCatalog(host.Paths.Workspace).ContentPath("qcow-seed"))) == original, "Immutable QCOW2 seed changed.");
        });
        await check("shared QCOW2 slot refuses mutation and leaves its catalog seed intact", async () =>
        {
            await using var host = new AgentFixture(); await setup(host);
            var definition = await Definition(host, "shared");
            var bytes = Qcow(seed());
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2 * 4096 + 9 * 2), 2);
            var original = DigestBytes(bytes); SetDisk(host, definition, "shared-seed", bytes);
            var refused = false;
            try { _ = await RuntimeStateManager.MaterializeAsync(definition, host.Draft("seed"), host.Paths.Workspace, "shared-run", CancellationToken.None); }
            catch (InvalidDataException) { refused = true; }
            Require(refused, "Shared QCOW2 clusters were silently overwritten.");
            Require(DigestBytes(File.ReadAllBytes(new DiskAssetCatalog(host.Paths.Workspace).ContentPath("shared-seed"))) == original, "Rejected injection changed the source.");
        });
        await check("guest receipt and exact selected IDs gate subset results", async () =>
        {
            await using var host = new AgentFixture(); await setup(host);
            var definition = await Definition(host, "coverage");
            var runtime = await RuntimeStateManager.MaterializeAsync(definition, host.Draft("seed"), host.Paths.Workspace, "coverage-run", CancellationToken.None);
            var plan = definition.Xiso!; var image = Path.Combine(runtime!.Directory, "hdd.qcow2");
            var reference = await File.ReadAllBytesAsync(Path.Combine(host.Draft("seed"), "reference.json"));
            using var source = JsonDocument.Parse(reference);
            var selected = source.RootElement.EnumerateArray().Where(x => plan.Tests.Contains(x.GetProperty("id").GetString())).ToArray();
            var raw = JsonSerializer.SerializeToUtf8Bytes(selected);
            var receipt = JsonSerializer.SerializeToUtf8Bytes(new { schema_version = 1, plan_id = plan.PlanId, selected_leaf_count = 1, emitted_leaf_count = 1, completion = "COMPLETE" });
            var bytes = await File.ReadAllBytesAsync(image);
            PutFile(bytes, 12352, "results.txt", 20, raw); PutFile(bytes, 12416, "resolved-plan-result.json", 21, receipt);
            await File.WriteAllBytesAsync(image, bytes);
            var results = Path.Combine(host.Paths.Results, "coverage-run"); Directory.CreateDirectory(results);
            XisoGuestEvidence.PreservePreparation(runtime.Directory, results);
            using (var disk = GuestDiskReader.Open(image, CancellationToken.None))
            {
                var evaluation = XisoGuestEvidence.Evaluate(plan, new FatxResultReader(disk, 0, seed().Length), raw, reference, results, CancellationToken.None);
                Require(evaluation.Checks.All(x => x.Passed), "A correct subset was compared against the full unselected oracle set.");
                Require(evaluation.Measurements.Count > 0, "Qualified subset lost its measurements.");
            }
            var extra = JsonSerializer.SerializeToUtf8Bytes(source.RootElement.EnumerateArray().Take(2).ToArray());
            var results2 = Path.Combine(host.Paths.Results, "extra-run"); Directory.CreateDirectory(results2);
            using (var disk = GuestDiskReader.Open(image, CancellationToken.None))
            {
                var evaluation = XisoGuestEvidence.Evaluate(plan, new FatxResultReader(disk, 0, seed().Length), extra, reference, results2, CancellationToken.None);
                Require(evaluation.Checks.Any(x => x.Name == "xiso_exact_coverage" && !x.Passed), "Unexpected emitted leaves were ignored.");
                Require(evaluation.Measurements.Count == 0, "Wrong coverage produced qualified metrics.");
            }
        });
        await check("campaign continues after an archived crash without replaying it", async () =>
        {
            await using var host = new AgentFixture(); await setup(host);
            await host.Json("/api/v1/xiso-campaigns", HttpMethod.Post, new { id = "continue", application = "application", tests = new[] { "cpu.direct", "shader_lifecycle.pipeline_train" } });
            await host.Json("/api/v1/xiso-campaigns/continue/start", HttpMethod.Post, new { }, HttpStatusCode.Accepted);
            host.State.SetPhase("idle"); await Queued(host, "xc-continue-001"); host.State.SetPhase("fixture");
            var source = Path.Combine(host.Paths.Pending, "agent-xc-continue-001");
            var target = Path.Combine(host.Paths.Tested, "agent-xc-continue-001"); Directory.Move(source, target);
            Directory.CreateDirectory(Path.Combine(host.Paths.Results, "crashed-first"));
            AtomicJson.Write(Path.Combine(target, ".runner-attempt.json"), new { RunId = "crashed-first", Phase = "finalized", Attempt = 1 });
            host.Assessment("crashed-first", new RunAssessment(ExecutionOutcome.Crashed, CorrectnessOutcome.Failed, EvidenceOutcome.Incomplete, ComparisonEligibility.Ineligible, [], []));
            host.State.SetPhase("idle"); await Queued(host, "xc-continue-002");
            var result = await host.Json("/api/v1/xiso-campaigns/continue");
            Require(result.GetProperty("failed").GetInt32() == 1 && result.GetProperty("finished").GetInt32() == 1, "Crash disappeared from campaign coverage.");
            Require(!Directory.Exists(source), "Archived attempt was requeued.");
        });
        await check("authorized campaign resumes an interrupted child creation handoff", async () =>
        {
            await using var host = new AgentFixture(); await setup(host);
            await host.Json("/api/v1/xiso-campaigns", HttpMethod.Post, new { id = "handoff", application = "application", tests = new[] { "cpu.direct" } });
            var plan = await host.Json("/api/v1/xiso-campaigns/handoff?view=plan"); var chunk = plan.GetProperty("chunks")[0];
            await host.Json("/api/v1/test-runs", HttpMethod.Post, new { id = "xc-handoff-001", applicationJobId = "application", testId = chunk.GetProperty("testId").GetString(), revision = chunk.GetProperty("revision").GetString() });
            await host.Json("/api/v1/xiso-campaigns/handoff/start", HttpMethod.Post, new { }, HttpStatusCode.Accepted);
            host.State.SetPhase("idle"); await Queued(host, "xc-handoff-001");
        });
        await RecoveryChecks.Run(check, setup);
    }
    private static async Task<RuntimeStateDefinition> Definition(AgentFixture host, string id)
    {
        await host.Json("/api/v1/xiso-campaigns", HttpMethod.Post, new { id, application = "application", tests = new[] { "cpu.direct" } });
        var plan = await host.Json("/api/v1/xiso-campaigns/" + id + "?view=plan");
        return JsonSerializer.Deserialize<RuntimeStateDefinition>(plan.GetProperty("chunks")[0].GetProperty("runtimeState").GetRawText(), ConfigLoader.JsonOptions)!;
    }
    private static async Task Queued(AgentFixture host, string id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            if (Directory.Exists(Path.Combine(host.Paths.Pending, "agent-" + id))) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Campaign child was not queued: " + id);
    }
    private static void SetDisk(AgentFixture host, RuntimeStateDefinition definition, string id, byte[] bytes)
    {
        var catalog = new DiskAssetCatalog(host.Paths.Workspace);
        var asset = catalog.CreateOrGet(id, "xiso-seed", bytes.Length, DigestBytes(bytes), null, out _);
        File.WriteAllBytes(catalog.ContentPath(id), bytes);
        definition.DiskAssets[0].AssetId = id; definition.DiskAssets[0].ExpectedSha256 = asset.Sha256;
    }
    private static byte[] Qcow(byte[] raw)
    {
        const int cluster = 4096; var count = raw.Length / cluster;
        var bytes = new byte[(count + 5) * cluster];
        void U32(int at, uint n) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at), n);
        void U64(int at, ulong n) => BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(at), n);
        U32(0, 0x514649fb); U32(4, 2); U32(20, 12); U64(24, (ulong)raw.Length); U32(36, 1);
        U64(40, 3 * cluster); U64(48, cluster); U32(56, 1);
        U64(cluster, 2 * cluster); U64(3 * cluster, (1UL << 63) | (4UL * cluster));
        for (var i = 0; i < count + 5; i++) BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2 * cluster + i * 2), 1);
        for (var i = 0; i < count; i++) U64(4 * cluster + i * 8, (1UL << 63) | (ulong)(uint)((i + 5) * cluster));
        raw.CopyTo(bytes, 5 * cluster); return bytes;
    }
    private static void PutFile(byte[] disk, int entry, string name, int firstCluster, byte[] content)
    {
        Require(content.Length < 4096, "Fixture result unexpectedly needs multiple clusters.");
        disk.AsSpan(entry, 64).Clear(); disk[entry] = (byte)name.Length;
        System.Text.Encoding.ASCII.GetBytes(name).CopyTo(disk, entry + 2);
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(entry + 44), (uint)firstCluster);
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(entry + 48), (uint)content.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(4096 + firstCluster * 2), 0xffff);
        content.CopyTo(disk, 8192 + (firstCluster - 1) * 4096);
    }
    private static string DigestBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
