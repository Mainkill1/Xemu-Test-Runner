using System.Security.Cryptography;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Runtime;

internal static class XisoPlanInjector
{
    public static int Inspect(string image, long partition, long length, CancellationToken ct)
    {
        using var disk = GuestDiskReader.Open(image, ct);
        var reader = new FatxResultReader(disk, partition, length);
        if (reader.ReadFile("xemu_perf_tests/results.txt", 16 * 1024 * 1024) is not null ||
            reader.ReadFile("xemu_perf_tests/resolved-plan-result.json", 65536) is not null)
            throw new InvalidDataException("xiso_seed_stale: use a clean immutable seed without previous guest results.");
        return reader.PlanSlot().Sum(x => x.Length);
    }

    public static void Inject(XisoExecution plan, string runtimeDirectory, CancellationToken ct)
    {
        plan.Validate();
        var path = RuntimeStateManager.ResolveInside(runtimeDirectory, plan.Image);
        RunStateInventory.NoLinks(path);
        GuestExtent[] extents;
        using (var disk = GuestDiskReader.Open(path, ct))
            extents = new FatxResultReader(disk, plan.PartitionOffsetBytes, plan.PartitionLengthBytes).PlanSlot();
        _ = Inspect(path, plan.PartitionOffsetBytes, plan.PartitionLengthBytes, ct);
        var config = plan.ConfigBytes();
        var capacity = extents.Sum(x => x.Length);
        if (config.Length > capacity) throw new InvalidDataException("xiso_plan_too_large: the prepared seed's JSON slot is too small for this selection.");
        var padded = new byte[capacity]; Array.Fill(padded, (byte)' '); config.CopyTo(padded, 0);
        PreallocatedDiskWriter.Write(path, extents, padded, ct);
        byte[] actual;
        using (var disk = GuestDiskReader.Open(path, ct))
            actual = new FatxResultReader(disk, plan.PartitionOffsetBytes, plan.PartitionLengthBytes).ReadFile(FatxResultReader.PlanPath, 1024 * 1024)
                ?? throw new InvalidDataException("Injected XISO configuration disappeared.");
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(actual), SHA256.HashData(padded)))
            throw new InvalidDataException("XISO configuration readback hash mismatch.");
        File.WriteAllBytes(Path.Combine(runtimeDirectory, "xiso-guest-config.json"), config);
        AtomicJson.Write(Path.Combine(runtimeDirectory, "xiso-preparation.json"), new
        {
            schemaVersion = 1, planId = plan.PlanId, plan.CatalogId, plan.IsoSha256,
            image = plan.Image, guestPath = FatxResultReader.PlanPath, selectedLeaves = plan.Tests.Length,
            payloadSha256 = XisoData.Sha(config), slotSha256 = XisoData.Sha(actual), slotBytes = actual.Length,
            verified = true, method = "private-preallocated-file-readback"
        });
    }
}
