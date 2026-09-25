using System.Buffers.Binary;

namespace XemuTestRunner.Runtime;

/// <summary>Write existing unique data clusters only. Reject snapshots, backing chains and unsupported features.</summary>
internal static class PreallocatedDiskWriter
{
    private const ulong Mask = 0x00fffffffffffe00UL;
    private const ulong Copied = 1UL << 63;
    public static void Write(string path, GuestExtent[] extents, byte[] bytes, CancellationToken ct)
    {
        RunStateInventory.NoLinks(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var header = Read(file, 0, 104);
        var qcow = BinaryPrimitives.ReadUInt32BigEndian(header) == 0x514649fb;
        var mapped = new List<GuestExtent>();
        if (!qcow) mapped.AddRange(extents);
        else
        {
            var version = U32(header, 4); var bits = U32(header, 20);
            if (version is not (2 or 3) || bits is < 9 or > 21 || U64(header, 8) != 0 || U32(header, 16) != 0 || U32(header, 32) != 0 || U32(header, 60) != 0 || U64(header, 64) != 0)
                throw new InvalidDataException("XISO injection requires a plain self-contained QCOW2 without encryption or internal snapshots.");
            if (version == 3 && (U64(header, 72) != 0 || U64(header, 80) != 0 || U64(header, 88) != 0 || U32(header, 96) != 4 || U32(header, 100) != 104))
                throw new InvalidDataException("Unsupported QCOW2 features for bounded plan injection.");
            var cluster = 1L << (int)bits;
            var l1 = checked((long)U64(header, 40)); var l1Size = U32(header, 36);
            var refTable = checked((long)U64(header, 48)); var refClusters = U32(header, 56);
            var metadata = new HashSet<long> { 0 };
            AddMetadata(l1, checked((long)l1Size * 8));
            AddMetadata(refTable, checked((long)refClusters * cluster));
            var dataClusters = new HashSet<long>();
            foreach (var extent in extents)
            {
                var offset = extent.Offset; var left = extent.Length;
                GuestDiskReader.Range(offset, left, checked((long)U64(header, 24)));
                while (left > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var index = offset / cluster; var within = offset % cluster;
                    var l1Index = index / (cluster / 8);
                    if (l1Index >= l1Size) throw new InvalidDataException("QCOW2 slot exceeds L1 coverage.");
                    var e1 = U64(Read(file, l1 + l1Index * 8, 8), 0);
                    if ((e1 & ~Mask & ~Copied) != 0 || (e1 & Mask) == 0) throw new InvalidDataException("Unallocated/unsupported QCOW2 L1 slot.");
                    var l2 = checked((long)(e1 & Mask)); AddMetadata(l2, cluster);
                    var e2 = U64(Read(file, l2 + (index % (cluster / 8)) * 8, 8), 0);
                    if ((e2 & Copied) == 0 || (e2 & ~(Mask | Copied)) != 0 || (e2 & Mask) == 0)
                        throw new InvalidDataException("XISO plan slot must use allocated, uncompressed, unique QCOW2 clusters.");
                    var data = checked((long)(e2 & Mask)); Aligned(data, cluster);
                    var physicalIndex = data / cluster;
                    var refIndex = physicalIndex / (cluster / 2);
                    if (refIndex >= refClusters * cluster / 8) throw new InvalidDataException("QCOW2 refcount table is too small.");
                    var refBlock = checked((long)U64(Read(file, refTable + refIndex * 8, 8), 0));
                    AddMetadata(refBlock, cluster);
                    var references = BinaryPrimitives.ReadUInt16BigEndian(Read(file, refBlock + physicalIndex % (cluster / 2) * 2, 2));
                    if (references != 1) throw new InvalidDataException("Shared QCOW2 data cluster cannot be edited in place.");
                    dataClusters.Add(data);
                    var take = checked((int)Math.Min(left, cluster - within));
                    mapped.Add(new(data + within, take)); offset += take; left -= take;
                }
            }
            if (dataClusters.Overlaps(metadata)) throw new InvalidDataException("QCOW2 slot overlaps image metadata.");
            void Aligned(long offset, long length)
            {
                if (offset < cluster || offset % cluster != 0) throw new InvalidDataException("Unaligned QCOW2 allocation.");
                GuestDiskReader.Range(offset, length, file.Length);
            }
            void AddMetadata(long offset, long length)
            {
                Aligned(offset, length);
                if (length > 32 * 1024 * 1024) throw new InvalidDataException("QCOW2 metadata exceeds the injection budget.");
                for (long at = offset; at < offset + length; at += cluster) metadata.Add(at);
            }
        }
        // Validate the entire write set before touching a byte.
        var sorted = mapped.OrderBy(x => x.Offset).ToArray();
        for (var i = 0; i < sorted.Length; i++)
        {
            GuestDiskReader.Range(sorted[i].Offset, sorted[i].Length, file.Length);
            if (i > 0 && sorted[i].Offset < sorted[i - 1].Offset + sorted[i - 1].Length)
                throw new InvalidDataException("Aliased disk extents in configuration slot.");
        }
        if (mapped.Sum(x => x.Length) != bytes.Length) throw new InvalidDataException("Configuration slot length mismatch.");
        var position = 0;
        foreach (var extent in mapped)
        { ct.ThrowIfCancellationRequested(); file.Position = extent.Offset; file.Write(bytes, position, extent.Length); position += extent.Length; }
        file.Flush(true);
    }
    private static byte[] Read(FileStream file, long offset, int length)
    { GuestDiskReader.Range(offset, length, file.Length); var bytes = new byte[length]; file.Position = offset; file.ReadExactly(bytes); return bytes; }
    private static uint U32(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at));
    private static ulong U64(byte[] bytes, int at) => BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(at));
}
