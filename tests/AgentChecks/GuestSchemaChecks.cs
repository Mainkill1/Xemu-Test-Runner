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
                await Check(candidate.ToJsonString(), reference.ToJsonString(), false);
            }));
        foreach (var encodedReference in new[] { false, true })
            checks.Add(($"equivalent guest metadata encodings preserve correctness (encoded reference {encodedReference})", async () =>
            {
                var reference = JsonNode.Parse(GuestHddChecks.Record())!.AsArray();
                var candidate = JsonNode.Parse(GuestHddChecks.Record())!.AsArray();
                const string metadata = "{\"oracle_status\":\"PASS\",\"checks\":[{\"outcome\":\"PASS\"}]}";
                reference[0]!["metadata"] = encodedReference ? JsonValue.Create(metadata) : JsonNode.Parse(metadata);
                candidate[0]!["metadata"] = encodedReference ? JsonNode.Parse(metadata) : JsonValue.Create(metadata);
                await Check(candidate.ToJsonString(), reference.ToJsonString(), true);
            }));
        checks.Add(("legacy guest records without optional outcome annotations remain valid", async () =>
        {
            var reference = JsonNode.Parse(GuestHddChecks.Record())!.AsArray();
            reference[0]!.AsObject().Remove("outcome");
            await Check(reference.ToJsonString(), reference.ToJsonString(), true);
        }));
        checks.Add(("missing reference-required oracle inside a metadata array is rejected", async () =>
        {
            var reference = JsonNode.Parse(GuestHddChecks.Record())!.AsArray();
            var candidate = JsonNode.Parse(GuestHddChecks.Record())!.AsArray();
            reference[0]!["metadata"] = JsonNode.Parse("{\"checks\":[{\"oracle_status\":\"PASS\"}]}");
            candidate[0]!["metadata"] = JsonNode.Parse("{\"checks\":[]}");
            await Check(candidate.ToJsonString(), reference.ToJsonString(), false);
        }));
    }

    private static async Task Check(string payload, string reference, bool expectedPass)
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
            if (expectedPass)
            {
                Require(evaluation.Correctness == CorrectnessOutcome.Passed && evaluation.Evidence == EvidenceOutcome.Complete,
                    "Equivalent or legacy-compatible guest evidence was incorrectly rejected.");
                Require(evaluation.Measurements.Count == 5, "A qualified leaf must retain all five timing statistics.");
            }
            else
            {
                Require(evaluation.Correctness != CorrectnessOutcome.Passed || evaluation.Evidence != EvidenceOutcome.Complete,
                    "Changed timing/oracle semantics qualified as a successful test.");
                Require(evaluation.Measurements.Count == 0, "Changed semantics produced guest timing claims.");
            }
            Require(await File.ReadAllTextAsync(Path.Combine(result, "guest", "results.txt")) == payload, "Raw guest evidence was discarded.");
        }
        finally { Directory.Delete(root, true); }
    }
}
