namespace XemuTestRunner.Runtime;

internal sealed record GuestExtent(long Offset, int Length);
internal sealed partial class FatxResultReader
{
    // This is deliberately NOT an arbitrary FATX write API. A clean seed must
    // already contain this one allocated file; no directory/FAT allocation changes.
    internal const string PlanPath = "xemu_perf_tests/xemu_perf_tests_config.json";
    internal GuestExtent[] PlanSlot()
    {
        var parent = Find(_root, "xemu_perf_tests");
        if (parent is null || !parent.Directory) throw new InvalidDataException("xiso_seed_slot_missing: prepare a clean seed containing " + PlanPath);
        var file = Find(parent.Cluster, "xemu_perf_tests_config.json");
        if (file is null || file.Directory || file.Length is < 1024 or > 1024 * 1024)
            throw new InvalidDataException("xiso_seed_slot_missing: the JSON file must have 1 KiB..1 MiB of preallocated logical length (whitespace padded).");
        var directories = Chain(_root).Concat(Chain(parent.Cluster)).ToHashSet();
        var result = new List<GuestExtent>();
        var remaining = checked((int)file.Length);
        foreach (var cluster in Chain(file.Cluster))
        {
            if (remaining <= 0 || directories.Contains(cluster)) throw new InvalidDataException("Invalid or cross-linked FATX configuration allocation.");
            var length = Math.Min(remaining, _clusterBytes);
            result.Add(new(_partition + ClusterOffset(cluster), length)); remaining -= length;
        }
        if (remaining != 0) throw new InvalidDataException("Truncated FATX configuration allocation.");
        return result.ToArray();
    }
}
