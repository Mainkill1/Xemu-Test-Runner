using System.Buffers.Binary;
using System.Text;
using XemuTestRunner.Reliability;

namespace XemuTestRunner.Runtime;

/// <summary>
/// Boot-only preparation: edit one fixed FATX config path in a private disk,
/// then repack allocated blocks as a self-contained QCOW2. No mount, guest
/// networking, backing chain or writes to the shared source are involved.
/// </summary>
public static class XisoDiskPreparer
{
    public const string ConfigPath = "xemu_perf_tests/xemu_perf_tests_config.json";
    public const string ReceiptPath = "xemu_perf_tests/resolved-plan-result.json";
    private const int PageBytes = 65536;
    private const long MaximumSeedBytes = 32L * 1024 * 1024;

    public static void Prepare(XisoPlanDefinition plan, string runtimeDirectory, CancellationToken ct)
    {
        plan.Validate();
        var image = RuntimeStateManager.ResolveInside(runtimeDirectory, plan.Image);
        RunStateInventory.NoLinks(image);
        if (!File.Exists(image) || new FileInfo(image).Length > MaximumSeedBytes)
            throw new InvalidDataException("XISO preparation needs a small clean self-contained seed (at most 32 MiB physically stored), not a game HDD or VM snapshot carrier.");
        var temporary = image + ".prepare-" + Guid.NewGuid().ToString("N");
        var config = Encoding.UTF8.GetBytes(plan.ConfigJson);
        try
        {
            using (var disk = GuestDiskReader.Open(image, ct))
            {
                var reader = new FatxResultReader(disk, plan.PartitionOffsetBytes, plan.PartitionLengthBytes);
                if (reader.ReadFile("xemu_perf_tests/results.txt", 16 * 1024 * 1024) is not null ||
                    reader.ReadFile(ReceiptPath, 65536) is not null)
                    throw new InvalidDataException("XISO seed contains stale results. A clean immutable seed is required.");
                var pages = new VirtualPages(disk, ct);
                var fatx = new ConfigWriter(pages, plan.PartitionOffsetBytes, plan.PartitionLengthBytes);
                fatx.WriteConfig(config);
                WriteQcow(image, temporary, disk, pages, ct);
            }
            using (var verify = GuestDiskReader.Open(temporary, ct))
            {
                var recovered = new FatxResultReader(verify, plan.PartitionOffsetBytes, plan.PartitionLengthBytes)
                    .ReadFile(ConfigPath, 1024 * 1024);
                if (recovered is null || !recovered.AsSpan().SequenceEqual(config))
                    throw new InvalidDataException("Prepared XISO configuration did not survive read-back verification.");
            }
            File.Move(temporary, image, overwrite: true); // Only this unlaunched private runtime image.
            using var prepared = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.Read);
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(prepared)).ToLowerInvariant();
            AtomicJson.Write(Path.Combine(runtimeDirectory, "xiso-preparation.json"), new
            {
                schemaVersion = 1, image = plan.Image, planSha256 = plan.PlanSha256, configSha256 = plan.ConfigSha256,
                catalogId = plan.CatalogId, preparedImageSha256 = digest, preparedBytes = prepared.Length,
                configuration = ConfigPath, readBackVerified = true, guestNetworkRequired = false,
                source = "private runtime clone", format = "self-contained-qcow2-v3"
            });
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class VirtualPages(GuestDiskReader disk, CancellationToken ct)
    {
        public SortedDictionary<long, byte[]> Changed { get; } = new();
        public void Read(long offset, Span<byte> output)
        {
            GuestDiskReader.Range(offset, output.Length, disk.Length);
            while (!output.IsEmpty)
            {
                ct.ThrowIfCancellationRequested();
                var page = offset / PageBytes; var within = (int)(offset % PageBytes);
                var count = Math.Min(output.Length, PageBytes - within);
                if (Changed.TryGetValue(page, out var bytes)) bytes.AsSpan(within, count).CopyTo(output);
                else disk.Read(offset, output[..count]);
                output = output[count..]; offset += count;
            }
        }
        public void Write(long offset, ReadOnlySpan<byte> input)
        {
            GuestDiskReader.Range(offset, input.Length, disk.Length);
            while (!input.IsEmpty)
            {
                ct.ThrowIfCancellationRequested();
                var page = offset / PageBytes; var within = (int)(offset % PageBytes);
                var count = Math.Min(input.Length, PageBytes - within);
                if (!Changed.TryGetValue(page, out var bytes))
                {
                    if (Changed.Count >= 512) throw new InvalidDataException("FATX config preparation exceeded its 32 MiB changed-page budget.");
                    bytes = new byte[PageBytes];
                    disk.Read(page * PageBytes, bytes.AsSpan(0, (int)Math.Min(PageBytes, disk.Length - page * PageBytes)));
                    Changed.Add(page, bytes);
                }
                input[..count].CopyTo(bytes.AsSpan(within, count));
                input = input[count..]; offset += count;
            }
        }
    }

    /// <summary>Only creates/replaces the known plan path, never a generic guest filesystem editor.</summary>
    private sealed class ConfigWriter
    {
        private readonly VirtualPages _pages;
        private readonly long _partition, _length, _fatBytes, _data, _clusters;
        private readonly int _clusterBytes, _entryBytes;
        private readonly uint _root;
        private uint _allocationCursor = 1;
        public ConfigWriter(VirtualPages pages, long partition, long length)
        {
            _pages = pages; _partition = partition; _length = length;
            Span<byte> super = stackalloc byte[16]; Read(0, super);
            if (!super[..4].SequenceEqual("FATX"u8)) throw new InvalidDataException("Expected FATX in the template's declared E partition.");
            var sectors = BinaryPrimitives.ReadUInt32LittleEndian(super[8..]);
            if (sectors is < 1 or > 128 || (sectors & (sectors - 1)) != 0) throw new InvalidDataException("Invalid FATX geometry.");
            _clusterBytes = checked((int)sectors * 512);
            var entries = length / _clusterBytes + 1;
            _entryBytes = entries < 0xfff0 ? 2 : 4;
            _fatBytes = (entries * _entryBytes + 4095) / 4096 * 4096;
            _data = 4096 + _fatBytes; _clusters = (length - _data) / _clusterBytes;
            _root = BinaryPrimitives.ReadUInt32LittleEndian(super[12..]); CheckCluster(_root);
        }
        public void WriteConfig(byte[] config)
        {
            var directory = Find(_root, "xemu_perf_tests");
            uint parent;
            if (directory.Found)
            {
                if (!directory.Directory) throw new InvalidDataException("XISO output path is not a directory.");
                parent = directory.Cluster;
            }
            else
            {
                parent = Allocate(1)[0];
                WriteEntry(directory.Offset, "xemu_perf_tests", parent, 0, directory: true);
            }
            var existing = Find(parent, "xemu_perf_tests_config.json");
            if (existing.Found)
            {
                if (existing.Directory) throw new InvalidDataException("XISO config path is a directory.");
                if (existing.Length > 1024 * 1024) throw new InvalidDataException("Existing guest config exceeds 1 MiB.");
                if (existing.Cluster != 0)
                    foreach (var cluster in Chain(existing.Cluster).ToArray()) SetNext(cluster, 0);
            }
            var allocated = Allocate((config.Length + _clusterBytes - 1) / _clusterBytes);
            var used = 0;
            foreach (var cluster in allocated)
            {
                var count = Math.Min(config.Length - used, _clusterBytes);
                Write(Cluster(cluster), config.AsSpan(used, count)); used += count;
            }
            WriteEntry(existing.Offset, "xemu_perf_tests_config.json", allocated[0], (uint)config.Length, directory: false);
        }
        private (bool Found, long Offset, uint Cluster, uint Length, bool Directory) Find(uint start, string name)
        {
            long free = -1; long last = -1;
            foreach (var cluster in Chain(start))
            {
                var buffer = new byte[_clusterBytes]; Read(Cluster(cluster), buffer);
                for (var at = 0; at < buffer.Length; at += 64)
                {
                    var count = buffer[at]; var position = Cluster(cluster) + at;
                    if (count is 0 or 0xff) return (false, free >= 0 ? free : position, 0, 0, false);
                    if (count == 0xe5) { if (free < 0) free = position; continue; }
                    if (count > 42) throw new InvalidDataException("Invalid FATX filename.");
                    if (Encoding.ASCII.GetString(buffer, at + 2, count).Equals(name, StringComparison.OrdinalIgnoreCase))
                        return (true, position, BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(at + 44)),
                            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(at + 48)), (buffer[at + 1] & 16) != 0);
                }
                last = cluster;
            }
            if (free >= 0) return (false, free, 0, 0, false);
            var extra = Allocate(1)[0];
            SetNext((uint)last, extra);
            return (false, Cluster(extra), 0, 0, false);
        }
        private uint[] Allocate(int count)
        {
            if (count < 1 || count > 2048) throw new InvalidDataException("XISO config allocation is outside its budget.");
            var found = new List<uint>();
            while (_allocationCursor <= _clusters && _allocationCursor <= 1048576 && found.Count < count)
            {
                var cluster = _allocationCursor++;
                if (Next(cluster) == 0) found.Add(cluster);
            }
            if (found.Count != count) throw new InvalidDataException("No free FATX space within the bounded config allocation window.");
            var zeros = new byte[_clusterBytes];
            for (var i = 0; i < found.Count; i++)
            {
                SetNext(found[i], i + 1 < found.Count ? found[i + 1] : _entryBytes == 2 ? 0xffffU : 0xffffffffU);
                Write(Cluster(found[i]), zeros);
            }
            return found.ToArray();
        }
        private IEnumerable<uint> Chain(uint start)
        {
            var seen = new HashSet<uint>(); var cluster = start;
            while (true)
            {
                CheckCluster(cluster);
                if (seen.Count >= 4096 || !seen.Add(cluster)) throw new InvalidDataException("Invalid/excessive FATX chain during plan preparation.");
                yield return cluster;
                var next = Next(cluster);
                if (next >= (_entryBytes == 2 ? 0xfff8U : 0xfffffff8U)) break;
                if (next == 0 || next >= (_entryBytes == 2 ? 0xfff0U : 0xfffffff0U)) throw new InvalidDataException("Unallocated FATX directory/config chain.");
                cluster = next;
            }
        }
        private uint Next(uint cluster)
        {
            Span<byte> bytes = stackalloc byte[4];
            Read(4096 + (long)cluster * _entryBytes, bytes[.._entryBytes]);
            return _entryBytes == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }
        private void SetNext(uint cluster, uint next)
        {
            Span<byte> bytes = stackalloc byte[4];
            if (_entryBytes == 2) BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)next);
            else BinaryPrimitives.WriteUInt32LittleEndian(bytes, next);
            Write(4096 + (long)cluster * _entryBytes, bytes[.._entryBytes]);
        }
        private void WriteEntry(long offset, string name, uint cluster, uint length, bool directory)
        {
            var bytes = new byte[64]; bytes[0] = (byte)name.Length; bytes[1] = directory ? (byte)16 : (byte)32;
            Encoding.ASCII.GetBytes(name).CopyTo(bytes, 2);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44), cluster);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48), length);
            // Deterministic FAT timestamps (1980-01-01), not host-local run time.
            foreach (var at in new[] { 54, 58, 62 }) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), 33);
            Write(offset, bytes);
        }
        private void CheckCluster(uint value) { if (value == 0 || value > _clusters) throw new InvalidDataException("FATX cluster out of bounds."); }
        private long Cluster(uint value) { CheckCluster(value); return _data + ((long)value - 1) * _clusterBytes; }
        private void Read(long at, Span<byte> data) { GuestDiskReader.Range(at, data.Length, _length); _pages.Read(_partition + at, data); }
        private void Write(long at, ReadOnlySpan<byte> data) { GuestDiskReader.Range(at, data.Length, _length); _pages.Write(_partition + at, data); }
    }

    private static SortedSet<long> AllocatedPages(string source, GuestDiskReader disk, CancellationToken ct)
    {
        var pages = new SortedSet<long>();
        using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[104]; file.ReadExactly(header);
        if (!header.AsSpan(0, 4).SequenceEqual(new byte[] { 0x51, 0x46, 0x49, 0xfb }))
        {
            for (long p = 0; p * PageBytes < disk.Length; p++) pages.Add(p);
            return pages;
        }
        if (BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(60)) != 0)
            throw new InvalidDataException("XISO preparation does not accept internal VM snapshots.");
        var bits = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20));
        var clusterBytes = 1 << bits;
        var l1Size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(36));
        var l1Offset = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(40));
        const ulong mask = 0x00fffffffffffe00UL;
        var entry = new byte[8];
        for (uint i = 0; i < l1Size; i++)
        {
            ct.ThrowIfCancellationRequested();
            file.Position = l1Offset + i * 8L; file.ReadExactly(entry);
            var table = (long)(BinaryPrimitives.ReadUInt64BigEndian(entry) & mask);
            if (table == 0) continue;
            GuestDiskReader.Range(table, clusterBytes, file.Length);
            var values = new byte[clusterBytes]; file.Position = table; file.ReadExactly(values);
            for (var j = 0; j < clusterBytes / 8; j++)
            {
                var value = BinaryPrimitives.ReadUInt64BigEndian(values.AsSpan(j * 8));
                if ((value & (1UL << 62)) != 0) throw new InvalidDataException("Compressed XISO seed clusters are unsupported.");
                if ((value & mask) == 0 || (value & 1UL) != 0) continue;
                var start = ((long)i * (clusterBytes / 8) + j) * clusterBytes;
                var end = Math.Min(start + clusterBytes, disk.Length);
                for (var p = start / PageBytes; p * PageBytes < end; p++) pages.Add(p);
                if (pages.Count > 512) throw new InvalidDataException("XISO seed expands beyond 32 MiB of data pages.");
            }
        }
        return pages;
    }

    private static void WriteQcow(string source, string destination, GuestDiskReader disk, VirtualPages pages, CancellationToken ct)
    {
        var allocated = AllocatedPages(source, disk, ct);
        allocated.UnionWith(pages.Changed.Keys);
        if (allocated.Count > 1024) throw new InvalidDataException("Prepared XISO image exceeds its data-page budget.");
        var tables = allocated.Select(p => p / 8192).Distinct().Order().ToArray();
        var l1Size = checked((int)((disk.Length + (1L << 29) - 1) >> 29));
        var l1Clusters = (l1Size * 8 + PageBytes - 1) / PageBytes;
        // At most 1024 data pages, 1024 L2 tables, and the bounded L1 table:
        // one 16-bit refcount block covers all physical clusters (<32768).
        var l1Start = 3L;
        var l2Start = l1Start + l1Clusters;
        var dataStart = l2Start + tables.Length;
        var totalClusters = dataStart + allocated.Count;
        if (totalClusters >= 32768) throw new InvalidDataException("Prepared QCOW2 metadata exceeds one refcount block.");
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        output.SetLength(totalClusters * PageBytes);
        var header = new byte[PageBytes];
        void U32(int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(at), value);
        void U64(int at, ulong value) => BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(at), value);
        U32(0, 0x514649fb); U32(4, 3); U32(20, 16); U64(24, (ulong)disk.Length);
        U32(36, (uint)l1Size); U64(40, (ulong)(l1Start * PageBytes)); U64(48, PageBytes); U32(56, 1);
        U32(96, 4); U32(100, 104); // 16-bit refcounts, v3 header.
        output.Write(header);
        var refTable = new byte[PageBytes]; BinaryPrimitives.WriteUInt64BigEndian(refTable, 2 * PageBytes);
        output.Write(refTable);
        var refcounts = new byte[PageBytes];
        for (var i = 0; i < totalClusters; i++) BinaryPrimitives.WriteUInt16BigEndian(refcounts.AsSpan(i * 2), 1);
        output.Write(refcounts);
        var l1 = new byte[l1Clusters * PageBytes];
        for (var i = 0; i < tables.Length; i++)
            BinaryPrimitives.WriteUInt64BigEndian(l1.AsSpan(checked((int)tables[i] * 8)), (1UL << 63) | (ulong)((l2Start + i) * PageBytes));
        output.Write(l1);
        var physical = allocated.Select((page, i) => (page, offset: (dataStart + i) * PageBytes)).ToDictionary(x => x.page, x => x.offset);
        foreach (var table in tables)
        {
            var l2 = new byte[PageBytes];
            foreach (var page in allocated.Where(p => p / 8192 == table))
                BinaryPrimitives.WriteUInt64BigEndian(l2.AsSpan((int)(page % 8192) * 8), (1UL << 63) | (ulong)physical[page]);
            output.Write(l2);
        }
        foreach (var page in allocated)
        {
            ct.ThrowIfCancellationRequested();
            var data = new byte[PageBytes];
            pages.Read(page * PageBytes, data.AsSpan(0, (int)Math.Min(PageBytes, disk.Length - page * PageBytes)));
            output.Write(data);
        }
        output.Flush(true);
    }
}
