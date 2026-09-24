using System.Buffers.Binary;
using System.Text;

namespace XemuTestRunner.Runtime;

/// <summary>Walk just the configured FATX path and its allocation chain; never enumerate the complete HDD.</summary>
internal sealed class FatxResultReader
{
    private readonly GuestDiskReader _disk;
    private readonly long _partition;
    private readonly long _length;
    private readonly int _clusterBytes;
    private readonly int _entryBytes;
    private readonly long _fatBytes;
    private readonly long _data;
    private readonly uint _root;
    private readonly long _dataClusters;
    private int _visited;

    public FatxResultReader(GuestDiskReader disk, long partitionOffset, long partitionLength)
    {
        _disk = disk; _partition = partitionOffset; _length = partitionLength;
        GuestDiskReader.Range(partitionOffset, partitionLength, disk.Length);
        var header = new byte[16];
        ReadPartition(0, header);
        if (!header.AsSpan(0,4).SequenceEqual("FATX"u8))
            throw new InvalidDataException("No original-Xbox FATX superblock at the configured partition offset.");
        var sectors = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        if (sectors is < 1 or > 128 || (sectors & (sectors - 1)) != 0)
            throw new InvalidDataException("Invalid FATX cluster size.");
        _clusterBytes = checked((int)sectors * 512);
        var entries = partitionLength / _clusterBytes + 1;
        _entryBytes = entries < 0xfff0 ? 2 : 4;
        _fatBytes = ((entries * _entryBytes + 4095) / 4096) * 4096;
        _data = 4096 + _fatBytes;
        _dataClusters = (partitionLength - _data) / _clusterBytes;
        _root = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        CheckCluster(_root);
    }

    public byte[]? ReadFile(string relative, int maximumBytes)
    {
        var parts = relative.Split('/');
        if (parts.Length is < 1 or > 16 || parts.Any(part => part.Length is < 1 or > 42 || part is "." or ".." || part.Contains('\\') || part.Contains(':')))
            throw new InvalidDataException("Invalid or too-deep FATX result path.");
        var cluster = _root;
        for (var i = 0; i < parts.Length; i++)
        {
            var entry = Find(cluster, parts[i]);
            if (entry is null) return null;
            if (i + 1 < parts.Length)
            {
                if (!entry.Directory) throw new InvalidDataException("A FATX path parent is not a directory.");
                cluster = entry.Cluster;
                continue;
            }
            if (entry.Directory) throw new InvalidDataException("The guest result path names a directory.");
            if (entry.Length > maximumBytes) throw new InvalidDataException("Guest result exceeds MaximumResultBytes.");
            if (entry.Length == 0) return [];
            var output = new byte[(int)entry.Length];
            var used = 0;
            foreach (var current in Chain(entry.Cluster))
            {
                if (used == output.Length) throw new InvalidDataException("Guest result allocation chain exceeds its declared length.");
                var count = Math.Min(_clusterBytes, output.Length - used);
                ReadPartition(ClusterOffset(current), output.AsSpan(used, count));
                used += count;
            }
            if (used != output.Length) throw new InvalidDataException("Guest result allocation chain ended early.");
            return output;
        }
        return null;
    }

    private Entry? Find(uint directory, string name)
    {
        Entry? match = null;
        var bytes = 0;
        var buffer = new byte[_clusterBytes];
        foreach (var cluster in Chain(directory))
        {
            bytes += _clusterBytes;
            if (bytes > 1024 * 1024) throw new InvalidDataException("Guest result parent directory exceeds its 1 MiB scan budget.");
            ReadPartition(ClusterOffset(cluster), buffer);
            for (var offset = 0; offset < buffer.Length; offset += 64)
            {
                var length = buffer[offset];
                if (length is 0 or 0xff) return match;
                if (length == 0xe5) continue;
                if (length > 42) throw new InvalidDataException("Invalid FATX directory entry name length.");
                var actual = Encoding.ASCII.GetString(buffer, offset + 2, length);
                if (!actual.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                if (match is not null) throw new InvalidDataException("Ambiguous FATX result path.");
                match = new(BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 44)),
                    BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 48)), (buffer[offset + 1] & 0x10) != 0);
            }
        }
        return match;
    }

    private IEnumerable<uint> Chain(uint first)
    {
        var seen = new HashSet<uint>();
        var cluster = first;
        while (true)
        {
            CheckCluster(cluster);
            if (!seen.Add(cluster)) throw new InvalidDataException("Cyclic FATX allocation chain.");
            if (++_visited > 65536) throw new InvalidDataException("FATX traversal exceeds the extraction budget.");
            yield return cluster;
            var next = Next(cluster);
            if (next == 0) yield break;
            cluster = next;
        }
    }
    private uint Next(uint cluster)
    {
        Span<byte> value = stackalloc byte[4];
        var offset = (long)cluster * _entryBytes;
        GuestDiskReader.Range(offset, _entryBytes, _fatBytes);
        ReadPartition(4096 + offset, value[.._entryBytes]);
        var next = _entryBytes == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(value) : BinaryPrimitives.ReadUInt32LittleEndian(value);
        if (next >= (_entryBytes == 2 ? 0xfff8U : 0xfffffff8U)) return 0;
        if (next == 0 || next >= (_entryBytes == 2 ? 0xfff0U : 0xfffffff0U))
            throw new InvalidDataException("Unallocated/bad FATX cluster in result chain.");
        return next;
    }
    private void CheckCluster(uint cluster)
    {
        if (cluster == 0 || cluster > _dataClusters) throw new InvalidDataException("FATX cluster is out of partition bounds.");
    }
    private long ClusterOffset(uint cluster) { CheckCluster(cluster); return _data + ((long)cluster - 1) * _clusterBytes; }
    private void ReadPartition(long offset, Span<byte> buffer)
    {
        GuestDiskReader.Range(offset, buffer.Length, _length);
        _disk.Read(_partition + offset, buffer);
    }
    private sealed record Entry(uint Cluster, uint Length, bool Directory);
}
