using System.Text.Json.Nodes;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;
using XemuTestRunner.Runtime;
using static AgentFixture;

internal static class GuestSchemaChecks
{
    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        foreach (var field in new[] { "unit", "direction", "outcome", "oracle_status" })
            checks.Add(($"guest {field} contract changes cannot produce qualified measurements", async () =>
            {
                var reference = JsonNode.Parse(GuestHddChecks.Record())!.AsArray();
                var candidate = JsonNode.Parse(GuestHddChecks.Record())!.AsArray();
                var record = candidate[0]!.AsObject();
                if (field == "unit") record["unit"] = "ms";
                if (field == "direction") record["direction"] = "higher_is_better";
                if (field == "outcome") record.Remove("outcome");
                if (field == "oracle_status")
                {
                    reference[0]!["metadata"] = new JsonObject { ["oracle_status"] = "PASS" };
                    record["metadata"] = new JsonObject();
                }
                await Reject(candidate.ToJsonString(), reference.ToJsonString());
            }));
    }

    private static async Task Reject(string payload, string reference)
    {
        var root = Path.Combine(Path.GetTempPath(), "guest-schema-" + Guid.NewGuid().ToString("N"));
        var package = Path.Combine(root, "package"); var runtime = Path.Combine(root, "runtime"); var result = Path.Combine(root, "schema-run");
        Directory.CreateDirectory(package); Directory.CreateDirectory(runtime); Directory.CreateDirectory(result);
        try
        {
            GuestDiskFixture.Create(Path.Combine(package, "seed.img"), null);
            GuestDiskFixture.Create(Path.Combine(runtime, "disk.img"), payload);
            await File.WriteAllTextAsync(Path.Combine(package, "reference.json"), reference);
            AtomicJson.Write(Path.Combine(package, AttemptJournal.FileName), new { RunId = "schema-run", Attempt = 1, Phase = "exited" });
            var job = new JobDefinition { Executable = "xemu.bin", Workload = new WorkloadContract { GuestHddResults = new GuestHddResultsDefinition {
                Image = "disk.img", PartitionLengthBytes = GuestDiskFixture.Size,
                ExpectedResults = "reference.json", ExpectedResultsSha256 = Digest(reference)
            }}};
            var evaluation = await WorkloadEvaluator.EvaluateAsync(job, package, result,
                new RuntimeMaterialization(runtime, [new RuntimeFileMaterialization("seed.img", "disk.img", GuestDiskFixture.Size, "fixture")]), 0, true, CancellationToken.None);
            Require(evaluation.Correctness != CorrectnessOutcome.Passed || evaluation.Evidence != EvidenceOutcome.Complete,
                "Changed timing/oracle semantics qualified as a successful test.");
            Require(evaluation.Measurements.Count == 0, "Changed semantics produced guest timing claims.");
            Require(await File.ReadAllTextAsync(Path.Combine(result, "guest", "results.txt")) == payload, "Invalid raw guest evidence was discarded.");
        }
        finally { Directory.Delete(root, true); }
    }
}
