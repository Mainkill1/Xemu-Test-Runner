using System.Buffers.Binary;
using System.Text;

namespace XemuTestRunner.Runtime;

public sealed record QcowSnapshotEntry(string Id, string Name, ulong VmStateBytes, bool HasVmState);

public static class QcowSnapshotDirectory
{
    // Metadata enumeration only. No cluster conversion, VM-state extraction or
    // assertion that a saved VM is compatible with another xemu build.
    // Format reference: https://www.qemu.org/docs/master/interop/qcow2.html
    public static IReadOnlyList<QcowSnapshotEntry> Read(Stream source)
    {
        var header = ReadAt(source, 0, 72);
        uint U32(int at) => BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(at));
        ulong U64(int at) => BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(at));
        var version = U32(4);
        if (U32(0) != 0x514649fb || version is not (2 or 3))
            throw new InvalidDataException("Snapshot carrier must be a QCOW2 v2 or v3 image.");
        if (U64(8) != 0 || U32(16) != 0 || U32(32) != 0)
            throw new InvalidDataException("Local snapshot import requires a self-contained, unencrypted carrier without a backing file.");
        var bits = U32(20);
        if (bits is < 9 or > 21) throw new InvalidDataException("Unsupported QCOW2 cluster size.");
        if (version == 3)
        {
            var extended = ReadAt(source, 72, 32);
            var features = BinaryPrimitives.ReadUInt64BigEndian(extended);
            var headerLength = BinaryPrimitives.ReadUInt32BigEndian(extended.AsSpan(28));
            // Dirty/corrupt images and external data cannot be an owned stable
            // snapshot carrier. Known compression/extended-L2 features don't
            // affect this directory reader; unknown incompatible bits do.
            if ((features & ~0x18UL) != 0 || headerLength < 104 || headerLength > (1U << (int)bits) || headerLength % 8 != 0)
                throw new InvalidDataException("QCOW2 carrier is dirty, corrupt, externally dependent or uses unsupported incompatible features.");
        }
        var count = U32(60);
        var offset = U64(64);
        if (count > 4096 || offset > long.MaxValue || (count != 0 && (offset < (1UL << (int)bits) || offset % (1UL << (int)bits) != 0)))
            throw new InvalidDataException("Invalid or over-budget QCOW2 snapshot directory.");
        var cursor = (long)offset;
        var start = cursor;
        var entries = new List<QcowSnapshotEntry>();
        for (var index = 0U; index < count; index++)
        {
            var entry = ReadAt(source, cursor, 40);
            var idLength = BinaryPrimitives.ReadUInt16BigEndian(entry.AsSpan(12));
            var nameLength = BinaryPrimitives.ReadUInt16BigEndian(entry.AsSpan(14));
            var extraLength = BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(36));
            if (idLength > 4096 || nameLength > 4096 || extraLength > 65536 || (version == 3 && extraLength < 16))
                throw new InvalidDataException("Invalid or over-budget QCOW2 snapshot entry.");
            var length = checked(40L + extraLength + idLength + nameLength);
            var next = checked((cursor + length + 7) & ~7L);
            if (next - start > 8 * 1024 * 1024 || next > source.Length)
                throw new InvalidDataException("Truncated or over-budget QCOW2 snapshot directory.");
            var vmBytes = (ulong)BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(32));
            if (extraLength >= 8)
                vmBytes = BinaryPrimitives.ReadUInt64BigEndian(ReadAt(source, cursor + 40, 8));
            var strings = ReadAt(source, cursor + 40 + extraLength, idLength + nameLength);
            var utf8 = new UTF8Encoding(false, true);
            string id, name;
            try { id = utf8.GetString(strings, 0, idLength); name = utf8.GetString(strings, idLength, nameLength); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("Snapshot names must be valid UTF-8.", error); }
            if (id.Any(char.IsControl) || name.Any(char.IsControl))
                throw new InvalidDataException("Snapshot names/IDs contain control characters.");
            entries.Add(new(id, name, vmBytes, vmBytes != 0));
            cursor = next;
        }
        return entries;
    }
    private static byte[] ReadAt(Stream source, long offset, int length)
    {
        if (!source.CanSeek || offset < 0 || offset > source.Length || length > source.Length - offset)
            throw new InvalidDataException("Truncated QCOW2 metadata.");
        source.Position = offset;
        var data = new byte[length];
        source.ReadExactly(data);
        return data;
    }
}
