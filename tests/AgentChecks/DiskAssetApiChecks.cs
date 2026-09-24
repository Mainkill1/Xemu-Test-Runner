using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static AgentFixture;

internal static class DiskAssetApiChecks
{
    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("disk assets are created uploaded listed and immutable without starting tests", async () =>
        {
            await using var host = new AgentFixture();
            var bytes = Encoding.UTF8.GetBytes("small-qcow2-seed");
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var created = await host.Json("/api/v1/disk-assets", HttpMethod.Post, new
            {
                id = "xiso-empty-v1", kind = "xiso-seed", length = bytes.LongLength, sha256 = sha,
                description = "Blank FATX seed"
            });
            Require(!created.GetProperty("ready").GetBoolean(), "Asset became ready before content upload.");

            using (var content = new ByteArrayContent(bytes))
            using (var response = await host.Client.PutAsync("/api/v1/disk-assets/xiso-empty-v1/content", content))
                Require(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

            var detail = await host.Json("/api/v1/disk-assets/xiso-empty-v1");
            Require(detail.GetProperty("ready").GetBoolean(), "Verified upload is not ready.");
            Require(detail.GetProperty("sha256").GetString() == sha && detail.GetProperty("kind").GetString() == "xiso-seed", "Asset identity changed.");
            var list = await host.Json("/api/v1/disk-assets");
            Require(list.GetProperty("items").GetArrayLength() == 1, "Catalog did not list the asset.");

            var same = await host.Json("/api/v1/disk-assets", HttpMethod.Post, new
            {
                id = "xiso-empty-v1", kind = "xiso-seed", length = bytes.LongLength, sha256 = sha,
                description = "Blank FATX seed"
            });
            Require(same.GetProperty("id").GetString() == "xiso-empty-v1", "Idempotent asset create changed identity.");
            var conflict = await host.Json("/api/v1/disk-assets", HttpMethod.Post, new
            {
                id = "xiso-empty-v1", kind = "xiso-seed", length = bytes.LongLength, sha256 = new string('0', 64)
            }, HttpStatusCode.Conflict);
            Require(conflict.GetProperty("code").GetString() == "disk_asset_conflict", "Changed manifest did not conflict.");
        }));

        checks.Add(("disk asset deletion refuses referenced saved jobs", async () =>
        {
            await using var host = new AgentFixture();
            var bytes = Encoding.UTF8.GetBytes("shared-seed");
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            await host.Json("/api/v1/disk-assets", HttpMethod.Post, new
            {
                id = "shared-hdd", kind = "xiso-seed", length = bytes.LongLength, sha256 = sha
            });
            using (var content = new ByteArrayContent(bytes))
            using (var response = await host.Client.PutAsync("/api/v1/disk-assets/shared-hdd/content", content))
                Require(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

            await host.Json("/api/v1/jobs", HttpMethod.Post, new
            {
                id = "asset-user",
                job = new
                {
                    id = "asset-user", executable = "xemu.bin", timeoutSeconds = 10,
                    runtimeState = new
                    {
                        enabled = true,
                        diskAssets = new[] { new { assetId = "shared-hdd", expectedSha256 = sha, destination = "xbox_hdd.qcow2" } }
                    }
                },
                files = new[] { new { path = "xemu.bin", length = 7, sha256 = Digest("fixture"), executable = true } }
            });

            var full = await host.Json("/api/v1/jobs/asset-user");
            Require(full.GetProperty("job").GetProperty("runtimeState").GetProperty("diskAssets").GetArrayLength() == 1,
                "Job did not preserve the disk asset reference.");
            var blocked = await host.Json("/api/v1/disk-assets/shared-hdd", HttpMethod.Delete, expected: HttpStatusCode.Conflict);
            Require(blocked.GetProperty("code").GetString() == "disk_asset_in_use", "Referenced asset deletion was not blocked.");
        }));

        checks.Add(("existing API job disk can be imported into the catalog without a network reupload", async () =>
        {
            await using var host = new AgentFixture();
            var bytes = Encoding.UTF8.GetBytes("existing-large-seed-fixture");
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            await host.Json("/api/v1/jobs", HttpMethod.Post, new
            {
                id = "legacy-seed",
                job = new { id = "legacy-seed", executable = "xemu.bin", timeoutSeconds = 10 },
                files = new[]
                {
                    new { path = "xemu.bin", length = 7L, sha256 = Digest("fixture"), executable = true },
                    new { path = "seeds/xbox_hdd.qcow2", length = bytes.LongLength, sha256 = sha, executable = false }
                }
            });
            using (var content = new ByteArrayContent(bytes))
            using (var response = await host.Client.PutAsync("/api/v1/jobs/legacy-seed/files/seeds/xbox_hdd.qcow2", content))
                Require(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

            await host.Json("/api/v1/disk-assets", HttpMethod.Post,
                new { id = "imported-seed", kind = "xiso-seed", length = bytes.LongLength, sha256 = sha });
            var imported = await host.Json("/api/v1/disk-assets/imported-seed/import", HttpMethod.Post,
                new { sourceJobId = "legacy-seed", path = "seeds/xbox_hdd.qcow2" });
            Require(imported.GetProperty("ready").GetBoolean(), "Local import did not publish a ready asset.");
            Require((await host.Json("/api/v1/disk-assets/imported-seed")).GetProperty("sha256").GetString() == sha,
                "Imported catalog identity changed.");
            var repeated = await host.Json("/api/v1/disk-assets/imported-seed/import", HttpMethod.Post,
                new { sourceJobId = "legacy-seed", path = "seeds/xbox_hdd.qcow2" });
            Require(repeated.GetProperty("ready").GetBoolean(),
                "Retrying a completed local import did not return the existing immutable asset.");
        }));

        checks.Add(("disk uploads obey active benchmark bulk-transfer policy", async () =>
        {
            await using var host = new AgentFixture();
            var bytes = Encoding.UTF8.GetBytes("seed");
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            await host.Json("/api/v1/disk-assets", HttpMethod.Post,
                new { id = "benchmark-seed", kind = "xiso-seed", length = bytes.LongLength, sha256 = sha });
            host.State.BeginJob("benchmark", "run", Environment.ProcessId,
                new XemuTestRunner.Runtime.OperationPolicyDefinition { Mode = "benchmark" });
            using var content = new ByteArrayContent(bytes);
            using var response = await host.Client.PutAsync("/api/v1/disk-assets/benchmark-seed/content", content);
            Require(response.StatusCode == HttpStatusCode.Conflict, "Disk upload bypassed benchmark transfer policy.");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Require(document.RootElement.GetProperty("code").GetString() == "operation_blocked", "Policy refusal lost its code.");
        }));
    }
}
