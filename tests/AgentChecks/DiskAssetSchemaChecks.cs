using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Runtime;
using static AgentFixture;

internal static class DiskAssetSchemaChecks
{
    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("adding disk assets preserves legacy runtime configuration bytes", () =>
        {
            // These are the exact pre-catalog fields and their declaration order.
            // Baked revisions hash ConfigLoader serialization, so an empty new
            // field must not invalidate every previously saved revision.
            var legacy = JsonSerializer.Serialize(new
            {
                Enabled = true,
                KeepOnSuccess = false,
                KeepOnFailure = true,
                Files = Array.Empty<RuntimeFileDefinition>()
            }, ConfigLoader.JsonOptions);
            var loaded = JsonSerializer.Deserialize<RuntimeStateDefinition>(legacy, ConfigLoader.JsonOptions)!;
            var current = JsonSerializer.Serialize(loaded, ConfigLoader.JsonOptions);
            Require(current == legacy, "Empty DiskAssets changed canonical legacy bytes and therefore saved-test hashes.");
            return Task.CompletedTask;
        }));
        checks.Add(("canonical serialization retains nonempty pinned disk references", () =>
        {
            var value = new RuntimeStateDefinition
            {
                Enabled = true,
                DiskAssets = [new RuntimeDiskAssetDefinition
                {
                    AssetId = "xiso-empty-v1", ExpectedSha256 = new string('a', 64), Destination = "xbox_hdd.qcow2"
                }]
            };
            var encoded = JsonSerializer.Serialize(value, ConfigLoader.JsonOptions);
            using var document = JsonDocument.Parse(encoded);
            Require(document.RootElement.GetProperty("DiskAssets").GetArrayLength() == 1, "Pinned disk disappeared from canonical evidence.");
            var decoded = JsonSerializer.Deserialize<RuntimeStateDefinition>(encoded, ConfigLoader.JsonOptions)!;
            Require(decoded.DiskAssets.Single().ExpectedSha256 == new string('a', 64), "Disk digest did not round trip.");
            return Task.CompletedTask;
        }));
    }
}
