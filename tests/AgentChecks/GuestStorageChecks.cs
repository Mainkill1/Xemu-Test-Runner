using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using XemuTestRunner.Config;
using XemuTestRunner.Queue;
using XemuTestRunner.Reliability;
using XemuTestRunner.Runtime;
using static AgentFixture;

internal static class GuestStorageChecks
{
    public static void Register(List<(string Name, Func<Task> Run)> checks)
    {
        checks.Add(("FATX32 extraction handles nonzero partition offsets with bounded reads", async () =>
        {
            await Exercise(4096, 512L * 1024 * 1024, (seed, disk) =>
            {
                Fat32(seed, null); Fat32(disk, GuestHddChecks.Record());
            }, true);
        }));
        checks.Add(("QCOW2 version two is read without conversion", async () =>
        {
            await Exercise(0, GuestDiskFixture.Size, (seed, disk) =>
            {
                GuestDiskFixture.Create(seed, null, true); GuestDiskFixture.Create(disk, GuestHddChecks.Record(), true);
                Write32(seed, 4, 2); Write32(disk, 4, 2);
            }, true);
        }));
        foreach (var defect in new[] { "backing", "dirty", "compressed", "outside" })
        {
            checks.Add(($"unsupported or corrupt QCOW2 {defect} fails without rewriting evidence", async () =>
            {
                await Exercise(0, GuestDiskFixture.Size, (seed, disk) =>
                {
                    GuestDiskFixture.Create(seed, null, true); GuestDiskFixture.Create(disk, GuestHddChecks.Record(), true);
                    if (defect == "backing") Write64(disk, 8, 104);
                    if (defect == "dirty") Write64(disk, 72, 1);
                    if (defect == "compressed") Write64(disk, 2 * 65536, (1UL << 62) | 196608UL);
                    if (defect == "outside") Write64(disk, 65536, 1UL << 40);
                }, false);
            }));
        }
        checks.Add(("invalid guest extraction configuration is rejected while deserializing a test", () =>
        {
            var invalid = new { Executable = "xemu.bin", Workload = new { GuestHddResults = new {
                Image = "../outside.img", PartitionOffsetBytes = 0, PartitionLengthBytes = GuestDiskFixture.Size,
                ExpectedResults = "reference.json", ExpectedResultsSha256 = new string('a',64)
            }}};
            var rejected = false;
            try { _ = JsonSerializer.Deserialize<JobDefinition>(JsonSerializer.Serialize(invalid), ConfigLoader.JsonOptions); }
            catch (Exception error) when (error is JsonException or InvalidDataException) { rejected = true; }
            Require(rejected, "Invalid guest extraction settings would survive until after the application ran.");
            return Task.CompletedTask;
        }));
    }

    private static async Task Exercise(long offset, long length, Action<string, string> create, bool success)
    {
        var root = Path.Combine(Path.GetTempPath(), "guest-storage-" + Guid.NewGuid().ToString("N"));
        var package = Path.Combine(root, "package"); var runtime = Path.Combine(root, "runtime");
        var result = Path.Combine(root, "storage-run");
        Directory.CreateDirectory(package); Directory.CreateDirectory(runtime); Directory.CreateDirectory(result);
        try
        {
            var seed = Path.Combine(package, "seed.img"); var disk = Path.Combine(runtime, "disk.img");
            create(seed, disk);
            var before = File.GetLastWriteTimeUtc(disk);
            var originalLength = new FileInfo(disk).Length;
            await File.WriteAllTextAsync(Path.Combine(package, "reference.json"), GuestHddChecks.Record());
            AtomicJson.Write(Path.Combine(package, AttemptJournal.FileName), new { RunId = "storage-run", Attempt = 1, Phase = "exited" });
            var job = new JobDefinition { Executable = "xemu.bin", Workload = new WorkloadContract { GuestHddResults = new GuestHddResultsDefinition {
                Image = "disk.img", PartitionOffsetBytes = offset, PartitionLengthBytes = length,
                ExpectedResults = "reference.json", ExpectedResultsSha256 = Digest(GuestHddChecks.Record())
            }}};
            var evaluation = await WorkloadEvaluator.EvaluateAsync(job, package, result,
                new RuntimeMaterialization(runtime, [new RuntimeFileMaterialization("seed.img", "disk.img", length, "fixture")]), 0, true, CancellationToken.None);
            Require(evaluation.Checks.Any(check => check.Name == "guest_hdd_extraction" && check.Passed == success),
                "Extraction status disagrees: " + string.Join("; ", evaluation.Checks.Select(check => check.Detail)));
            if (success)
            {
                Require(evaluation.Correctness == CorrectnessOutcome.Passed, "Guest oracle did not pass.");
                using var report = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result, "guest", "extraction.json")));
                Require(report.RootElement.GetProperty("imageBytesRead").GetInt64() < 128 * 1024, "Reader scanned the large disk.");
            }
            else Require(!File.Exists(Path.Combine(result, "guest", "results.txt")) && evaluation.Measurements.Count == 0, "Unsupported image produced result evidence.");
            Require(File.GetLastWriteTimeUtc(disk) == before && new FileInfo(disk).Length == originalLength, "Extraction changed its input image.");
        }
        finally { Directory.Delete(root, true); }
    }

    // Sparse synthetic partition: tests the FAT32 branch without allocating or
    // reading a physical 512 MiB file. This helper is never part of production.
    private static void Fat32(string path, string? content)
    {
        const long partition = 4096, length = 512L * 1024 * 1024;
        const int cluster = 4096;
        var fatBytes = (((length / cluster + 1) * 4 + 4095) / 4096) * 4096;
        var data = partition + 4096 + fatBytes;
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.SetLength(partition + length);
        var header = new byte[16]; "FATX"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 1);
        Write(partition, header);
        foreach (var number in new[] { 1, 2, 5 })
        {
            var entry = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(entry, uint.MaxValue);
            Write(partition + 4096 + number * 4, entry);
        }
        Write(data, Entry("xemu_perf_tests", 2, 0, true));
        if (content is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            Write(data + cluster, Entry("results.txt", 5, bytes.Length, false));
            Write(data + 4 * cluster, bytes);
        }
        void Write(long position, byte[] bytes) { file.Position = position; file.Write(bytes); }
    }
    private static byte[] Entry(string name, uint cluster, int length, bool directory)
    {
        var bytes = new byte[64]; bytes[0] = (byte)name.Length; bytes[1] = directory ? (byte)0x10 : (byte)0x20;
        Encoding.ASCII.GetBytes(name).CopyTo(bytes, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44), cluster);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(48), length);
        return bytes;
    }
    private static void Write32(string path, long position, uint value)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        file.Position = position; Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); file.Write(bytes);
    }
    private static void Write64(string path, long position, ulong value)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        file.Position = position; Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(bytes, value); file.Write(bytes);
    }
}
