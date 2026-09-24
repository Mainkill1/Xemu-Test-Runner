using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;
using XemuTestRunner.Runtime;
using static AgentFixture;

internal static class GuestHddChecks
{
    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        foreach (var qcow in new[] { false, true })
            checks.Add(($"guest results are extracted automatically from {(qcow ? "QCOW2" : "raw")} FATX without disk conversion", async () =>
            {
                await using var fixture = new GuestFixture(qcow);
                var result = await fixture.Evaluate();
                Require(result.Correctness == CorrectnessOutcome.Passed && result.Evidence == EvidenceOutcome.Complete, "Guest result was not validated.");
                Require(result.Measurements.Any(x => x.Name == "xiso/cpu/test/median_us" && x.Value == 20), "Guest timing not exposed to A/B.");
                var raw = Path.Combine(fixture.Results, "guest", "results.txt");
                Require(await File.ReadAllTextAsync(raw) == fixture.Payload, "Raw guest bytes were changed.");
                Require(Digest(await File.ReadAllTextAsync(raw)) == Digest(fixture.Payload), "Raw digest differs.");
                using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Results, "guest", "extraction.json")));
                Require(report.RootElement.GetProperty("imageBytesRead").GetInt64() < GuestDiskFixture.Size / 4, "Extractor scanned the disk instead of targeted reads.");
                Require(fixture.DiskHash == Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fixture.Disk))), "Extraction modified the HDD.");
            }));
        checks.Add(("guest extraction rejects a seed containing stale result files", async () =>
        {
            await using var fixture = new GuestFixture(stale: true);
            var result = await fixture.Evaluate();
            Require(result.Evidence != EvidenceOutcome.Complete && result.Measurements.Count == 0, "Old seed output was attributed to this attempt.");
        }));
        checks.Add(("guest extraction never reads a still-owned or held HDD", async () =>
        {
            await using var fixture = new GuestFixture();
            AtomicJson.Write(Path.Combine(fixture.Package, AttemptJournal.FileName), new { RunId = "guest-run", Attempt = 1, Phase = "held" });
            var result = await fixture.Evaluate();
            Require(result.Evidence != EvidenceOutcome.Complete && !File.Exists(Path.Combine(fixture.Results, "guest", "results.txt")), "Held image was read.");
        }));
        checks.Add(("partial guest JSON is preserved but never produces timing claims", async () =>
        {
            await using var fixture = new GuestFixture(payload: "[{\"id\":\"cpu.test\"");
            var result = await fixture.Evaluate();
            Require(result.Evidence != EvidenceOutcome.Complete && result.Measurements.Count == 0, "Partial file produced measurements.");
            Require(await File.ReadAllTextAsync(Path.Combine(fixture.Results, "guest", "results.txt")) == fixture.Payload, "Partial raw evidence was lost.");
        }));
        checks.Add(("wrong guest hashes or fixed work block correctness", async () =>
        {
            await using var fixture = new GuestFixture(payload: Record(checksum: "bad"));
            var result = await fixture.Evaluate();
            Require(result.Correctness == CorrectnessOutcome.Failed, "Incorrect guest work passed.");
        }));
        checks.Add(("missing expected guest records block evidence completeness", async () =>
        {
            await using var fixture = new GuestFixture(payload: "[]");
            var result = await fixture.Evaluate();
            Require(result.Evidence != EvidenceOutcome.Complete, "Missing suite records were accepted.");
        }));
        checks.Add(("cyclic FATX chains fail within bounds instead of hanging", async () =>
        {
            await using var fixture = new GuestFixture(cyclic: true, payload: Record() + new string(' ', 5000));
            var result = await fixture.Evaluate().WaitAsync(TimeSpan.FromSeconds(5));
            Require(result.Evidence != EvidenceOutcome.Complete, "Corrupt FAT chain was silently treated as complete.");
        }));
    }

    internal static string Record(string checksum = "abcd") => JsonSerializer.Serialize(new[] { new
    {
        schema_version = 1, id = "cpu.test", revision = 1, kind = "leaf", name = "Cpu::Test", outcome = "PASS",
        iterations = 3, sample_count = 3, measurement_iterations_multiplier = 1, warmup_iterations = 0,
        gpu_completion_mode = "per_iteration", raw_results = new[] { 10, 20, 30 }, work_checksum = checksum
    }});

    private sealed class GuestFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "guest-result-" + Guid.NewGuid().ToString("N"));
        private readonly JobDefinition _job;
        private readonly RuntimeMaterialization _runtime;
        public string Package { get; }
        public string Results { get; }
        public string Disk { get; }
        public string Payload { get; }
        public string DiskHash { get; }
        public GuestFixture(bool qcow = false, bool stale = false, bool cyclic = false, string? payload = null)
        {
            Package = Path.Combine(_root, "package"); Results = Path.Combine(_root, "guest-run");
            var runtime = Path.Combine(_root, "runtime");
            Directory.CreateDirectory(Package); Directory.CreateDirectory(Results); Directory.CreateDirectory(runtime);
            Payload = payload ?? Record();
            Disk = Path.Combine(runtime, "disk.img");
            GuestDiskFixture.Create(Path.Combine(Package, "seed.img"), stale ? Record() : null, qcow);
            GuestDiskFixture.Create(Disk, Payload, qcow, cyclic);
            DiskHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Disk)));
            File.WriteAllText(Path.Combine(Package, "reference.json"), Record());
            AtomicJson.Write(Path.Combine(Package, AttemptJournal.FileName), new { RunId = "guest-run", Attempt = 1, Phase = "exited" });
            var definition = new
            {
                Id = "guest", Executable = "xemu.bin",
                Workload = new { GuestHddResults = new {
                    Image = "disk.img", PartitionOffsetBytes = 0, PartitionLengthBytes = GuestDiskFixture.Size,
                    GuestPath = "xemu_perf_tests/results.txt", ExpectedResults = "reference.json",
                    ExpectedResultsSha256 = Digest(Record())
                }}
            };
            _job = JsonSerializer.Deserialize<JobDefinition>(JsonSerializer.Serialize(definition), ConfigLoader.JsonOptions)!;
            _runtime = new(runtime, [new RuntimeFileMaterialization("seed.img", "disk.img", GuestDiskFixture.Size, "fixture")]);
        }
        public Task<WorkloadEvaluation> Evaluate() => WorkloadEvaluator.EvaluateAsync(_job, Package, Results, _runtime, 0, true, CancellationToken.None);
        public ValueTask DisposeAsync() { Directory.Delete(_root, true); return ValueTask.CompletedTask; }
    }
}
