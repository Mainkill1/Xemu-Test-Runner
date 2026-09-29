using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using static AgentFixture;

internal static class LocalDiskImportChecks
{
    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("local snapshot import owns an intact copy and lists real VM snapshot names", async () =>
        {
            await using var host = new AgentFixture();
            var source = Path.Combine(host.Root, "xbox_hdd - GBTG.qcow2");
            var bytes = Carrier();
            await File.WriteAllBytesAsync(source, bytes);
            var expectedHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var started = await host.Json("/api/v1/disk-assets/ghoulies/import-local", HttpMethod.Post,
                new { path = source, description = "Ghoulies snapshot carrier" }, HttpStatusCode.Accepted);
            Require(started.GetProperty("id").GetString() == "ghoulies", "Import lost stable asset identity.");
            var result = await Finish(host, "ghoulies");
            Require(result.GetProperty("state").GetString() == "completed", result.ToString());
            var asset = await host.Json("/api/v1/disk-assets/ghoulies");
            Require(asset.GetProperty("ready").GetBoolean() && asset.GetProperty("kind").GetString() == "snapshot-carrier", "Import did not publish an immutable carrier.");
            Require(asset.GetProperty("sha256").GetString() == expectedHash, "Import hash is wrong.");
            var retained = Path.Combine(host.Root, "DiskAssets", "ghoulies", "disk.qcow2");
            Require((await File.ReadAllBytesAsync(retained)).SequenceEqual(bytes), "Import converted or damaged the carrier.");
            Require((await File.ReadAllBytesAsync(source)).SequenceEqual(bytes), "Import wrote to the source.");
            var snapshots = await host.Json("/api/v1/disk-assets/ghoulies/snapshots");
            var items = snapshots.GetProperty("items");
            Require(items.GetArrayLength() == 2, "Snapshot occurrences were lost.");
            Require(items[0].GetProperty("name").GetString() == "spider death" && items[0].GetProperty("hasVmState").GetBoolean(), "Whole VM snapshot name/state lost.");
            Require(!items[1].GetProperty("hasVmState").GetBoolean(), "Disk-only snapshot falsely claims VM state.");
            var retry = await host.Json("/api/v1/disk-assets/ghoulies/import-local", HttpMethod.Post,
                new { path = source, description = "Ghoulies snapshot carrier" }, HttpStatusCode.Accepted);
            Require(retry.GetProperty("state").GetString() == "completed", "Retry started another acquisition.");
            var different = await host.Json("/api/v1/disk-assets/ghoulies/import-local", HttpMethod.Post,
                new { path = Path.Combine(host.Root, "different.qcow2") }, HttpStatusCode.Conflict);
            Require(different.GetProperty("code").GetString() == "disk_asset_conflict", "Retargeted import was not rejected.");
            await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
            Require((await File.ReadAllBytesAsync(retained)).SequenceEqual(bytes), "Catalog still aliases mutable source.");
        }));
        checks.Add(("local snapshot import refuses outside roots and benchmark ownership", async () =>
        {
            await using var host = new AgentFixture();
            var outside = Path.Combine(Path.GetTempPath(), "outside.qcow2");
            await host.Json("/api/v1/disk-assets/outside/import-local", HttpMethod.Post,
                new { path = outside }, HttpStatusCode.BadRequest);
            host.State.BeginJob("benchmark", "run", Environment.ProcessId,
                new XemuTestRunner.Runtime.OperationPolicyDefinition { Mode = "benchmark" });
            var blocked = await host.Json("/api/v1/disk-assets/blocked/import-local", HttpMethod.Post,
                new { path = Path.Combine(host.Root, "source.qcow2") }, HttpStatusCode.Conflict);
            Require(blocked.GetProperty("code").GetString() == "operation_blocked", "Import bypassed benchmark policy.");
        }));
        checks.Add(("local import supports an approved external folder and refuses linked sources", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "approved-carrier-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                await using var host = new AgentFixture(http => http.LocalDiskImportRoots.Add(directory));
                var source = Path.Combine(directory, "external.qcow2");
                await File.WriteAllBytesAsync(source, Carrier());
                await host.Json("/api/v1/disk-assets/approved/import-local", HttpMethod.Post, new { path = source }, HttpStatusCode.Accepted);
                Require((await Finish(host, "approved")).GetProperty("state").GetString() == "completed", "Configured import root was ignored.");
                if (!OperatingSystem.IsWindows())
                {
                    var link = Path.Combine(host.Root, "linked.qcow2");
                    File.CreateSymbolicLink(link, source);
                    await host.Json("/api/v1/disk-assets/linked/import-local", HttpMethod.Post, new { path = link }, HttpStatusCode.BadRequest);
                }
            }
            finally { Directory.Delete(directory, recursive: true); }
        }));
        checks.Add(("explicit cancellation stops a large acquisition and preserves the original file", async () =>
        {
            await using var host = new AgentFixture();
            var path = Path.Combine(host.Root, "large.qcow2");
            await File.WriteAllBytesAsync(path, Carrier());
            using (var source = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None)) source.SetLength(128 * 1024 * 1024);
            await host.Json("/api/v1/disk-assets/large/import-local", HttpMethod.Post, new { path }, HttpStatusCode.Accepted);
            await host.Json("/api/v1/disk-assets/large/import-local", HttpMethod.Delete);
            var outcome = await Finish(host, "large");
            Require(outcome.GetProperty("state").GetString() == "cancelled", outcome.ToString());
            await host.Json("/api/v1/disk-assets/large", expected: HttpStatusCode.NotFound);
            Require(new FileInfo(path).Length == 128 * 1024 * 1024, "Cancellation touched the original carrier.");
            // The terminal cancellation receipt may be visible while the
            // worker still removes staging. Wait for that ownership release
            // before checking the distinct persisted-definition refusal.
            var persisted = false;
            for (var attempt = 0; attempt < 500; attempt++)
            {
                var conflict = await host.Json("/api/v1/disk-assets", HttpMethod.Post,
                    new { id = "large", kind = "readonly-input", length = 1, sha256 = new string('a', 64) }, HttpStatusCode.Conflict);
                var code = conflict.GetProperty("code").GetString();
                if (code == "disk_asset_conflict") { persisted = true; break; }
                Require(code == "disk_asset_source_busy", "Unexpected import-ID refusal.");
                await Task.Delay(10);
            }
            Require(persisted, "Cancelled import never released cleanup ownership.");
        }));
        checks.Add(("server shutdown releases all local import files before returning", async () =>
        {
            await using var host = new AgentFixture();
            var source = Path.Combine(host.Root, "shutdown.qcow2");
            await File.WriteAllBytesAsync(source, Carrier());
            using (var file = new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.None)) file.SetLength(128 * 1024 * 1024);
            await host.Json("/api/v1/disk-assets/shutdown/import-local", HttpMethod.Post, new { path = source }, HttpStatusCode.Accepted);
            await host.StopAsync();
            Require(!Directory.EnumerateFiles(Path.Combine(host.Root, ".disk-imports"), "*.partial").Any(), "Server released ownership while import staging survived.");
            using var exclusive = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Require(exclusive.Length == 128 * 1024 * 1024, "Shutdown changed the source carrier.");
        }));
        checks.Add(("restart reconciles only a matching publication and clears stale errors", async () =>
        {
            await using var host = new AgentFixture();
            var source = Path.Combine(host.Root, "recover.qcow2");
            await File.WriteAllBytesAsync(source, Carrier());
            await host.Json("/api/v1/disk-assets/recover/import-local", HttpMethod.Post, new { path = source }, HttpStatusCode.Accepted);
            Require((await Finish(host, "recover")).GetProperty("state").GetString() == "completed", "Fixture import failed.");
            var receiptPath = Path.Combine(host.Root, ".disk-imports", "recover.json");
            var receipt = System.Text.Json.JsonSerializer.Deserialize<XemuTestRunner.Runtime.LocalDiskImportReceipt>(await File.ReadAllTextAsync(receiptPath), XemuTestRunner.Config.ConfigLoader.JsonOptions)!;
            XemuTestRunner.Reliability.AtomicJson.Write(receiptPath, receipt with { State = "publishing", Error = "old interrupted error" });
            var restarted = new XemuTestRunner.Runtime.LocalDiskImportStore(host.Root, new XemuTestRunner.Config.HttpOptions(), new XemuTestRunner.Reliability.ActivityHub());
            Require(restarted.Get("recover") is { State: "completed", Error: null }, "Matching committed publication did not recover cleanly.");
            XemuTestRunner.Reliability.AtomicJson.Write(receiptPath, receipt with { State = "publishing", Sha256 = new string('0', 64) });
            Require(restarted.Get("recover")?.State == "interrupted", "Unrelated bytes became a completed acquisition.");
        }));
        checks.Add(("runner cancellation interrupts import without publishing or keeping staging bytes", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "cancel-carrier-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var source = Path.Combine(directory, "source.qcow2");
                await File.WriteAllBytesAsync(source, Carrier());
                using var stop = new CancellationTokenSource(); stop.Cancel();
                var store = new XemuTestRunner.Runtime.LocalDiskImportStore(directory, new XemuTestRunner.Config.HttpOptions(), new XemuTestRunner.Reliability.ActivityHub());
                store.Start("cancelled", source, null, stop.Token);
                for (var count = 0; count < 500 && store.IsRunning("cancelled"); count++) await Task.Delay(10);
                Require(store.Get("cancelled")?.State == "interrupted", "Shutdown became a completed import.");
                Require(new XemuTestRunner.Runtime.DiskAssetCatalog(directory).TryGet("cancelled") is null, "Cancelled import published an asset.");
                Require(!Directory.EnumerateFiles(Path.Combine(directory, ".disk-imports"), "*.partial").Any(), "Cancelled staging was retained.");
                // A new server lifetime can retry the same immutable acquisition.
                store.Start("cancelled", source, null, CancellationToken.None);
                for (var count = 0; count < 500 && store.IsRunning("cancelled"); count++) await Task.Delay(10);
                Require(store.Get("cancelled")?.State == "completed", "Interrupted import was not recoverable.");
            }
            finally { Directory.Delete(directory, recursive: true); }
        }));
        checks.Add(("local snapshot import rejects malformed and dependent QCOW2 without publication", async () =>
        {
            await using var host = new AgentFixture();
            var dirty = Carrier(); BinaryPrimitives.WriteUInt64BigEndian(dirty.AsSpan(72), 1);
            var external = Carrier(); BinaryPrimitives.WriteUInt64BigEndian(external.AsSpan(72), 4);
            var hugeCount = Carrier(); BinaryPrimitives.WriteUInt32BigEndian(hugeCount.AsSpan(60), 4097);
            var hugeExtra = Carrier(); BinaryPrimitives.WriteUInt32BigEndian(hugeExtra.AsSpan(548), uint.MaxValue);
            foreach (var (id, bytes) in new[] { ("bad-magic", new byte[4096]), ("truncated", Carrier()[..540]), ("backing", Carrier(backing: true)), ("dirty", dirty), ("external", external), ("count", hugeCount), ("extra", hugeExtra) })
            {
                var path = Path.Combine(host.Root, id + ".qcow2");
                await File.WriteAllBytesAsync(path, bytes);
                await host.Json($"/api/v1/disk-assets/{id}/import-local", HttpMethod.Post, new { path }, HttpStatusCode.BadRequest);
                await host.Json($"/api/v1/disk-assets/{id}", expected: HttpStatusCode.NotFound);
            }
        }));
    }
    private static async Task<System.Text.Json.JsonElement> Finish(AgentFixture host, string id)
    {
        for (var count = 0; count < 500; count++)
        {
            var value = await host.Json($"/api/v1/disk-assets/{id}/import-local");
            if (value.GetProperty("state").GetString() is not ("queued" or "running" or "cancelling" or "publishing")) return value;
            await Task.Delay(10);
        }
        throw new InvalidOperationException("Local fixture import did not finish.");
    }
    private static byte[] Carrier(bool backing = false)
    {
        // Independent fixture from QEMU's documented v3 header/snapshot table.
        var bytes = new byte[4096];
        void U32(int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at), value);
        void U64(int at, ulong value) => BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(at), value);
        U32(0, 0x514649fb); U32(4, 3); U32(20, 9); U64(24, 1024 * 1024); U32(96, 4); U32(100, 104);
        if (backing) { U64(8, 128); U32(16, 8); }
        U32(60, 2); U64(64, 512);
        var cursor = 512;
        foreach (var (id, name, size) in new[] { ("1", "spider death", (1UL << 34) + 123), ("2", "disk only", 0UL) })
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(cursor + 12), (ushort)id.Length);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(cursor + 14), (ushort)nameBytes.Length);
            U32(cursor + 36, 16); U64(cursor + 40, size); U64(cursor + 48, 1024 * 1024);
            Encoding.UTF8.GetBytes(id).CopyTo(bytes, cursor + 56);
            nameBytes.CopyTo(bytes, cursor + 56 + id.Length);
            cursor = (cursor + 56 + id.Length + nameBytes.Length + 7) & ~7;
        }
        return bytes;
    }
}
