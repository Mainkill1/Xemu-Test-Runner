using System.Buffers.Binary;
using System.Diagnostics;

namespace XemuTestRunner.Runtime;

/// <summary>
/// Small read-only block accessor. Raw and self-contained QCOW2 v2/v3 normal
/// clusters only; unsupported storage features fail instead of returning wrong bytes.
/// No mount, format, disk conversion, guest process, or image write is performed.
/// </summary>
internal sealed class GuestDiskReader : IDisposable
{
    private const ulong OffsetMask = 0x00fffffffffffe00UL;
    private const ulong Copied = 1UL << 63;
    private const ulong Compressed = 1UL << 62;
    private readonly FileStream _file;
    private readonly CancellationToken _ct;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly Dictionary<long, ulong> _entries = new();
    private int _clusterBytes;
    private int _version;
    private uint _l1Size;
    private long _l1Offset;
    public long Length { get; private set; }
    public long BytesRead { get; private set; }
    public string Format => _version == 0 ? "raw" : "qcow2-v" + _version;

    private GuestDiskReader(string path, CancellationToken ct)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        _ct = ct;
    }

    public static GuestDiskReader Open(string path, CancellationToken ct)
    {
        var disk = new GuestDiskReader(path, ct);
        try { disk.Initialize(); return disk; }
        catch { disk.Dispose(); throw; }
    }

    private void Initialize()
    {
        var header = new byte[104];
        Physical(0, header);
        if (BinaryPrimitives.ReadUInt32BigEndian(header) != 0x514649fb)
        {
            Length = _file.Length;
            return;
        }
        _version = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)));
        if (_version is not (2 or 3)) throw new InvalidDataException("Unsupported QCOW2 version.");
        if (BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8)) != 0 || BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16)) != 0)
            throw new InvalidDataException("QCOW2 backing chains are not supported by guest-result extraction. Use a self-contained private runtime image.");
        var bits = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20));
        if (bits is < 9 or > 21) throw new InvalidDataException("Unsupported QCOW2 cluster geometry.");
        _clusterBytes = 1 << (int)bits;
        var length = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(24));
        if (length is < 8192 or > (1UL << 44)) throw new InvalidDataException("QCOW2 virtual disk length is outside supported bounds.");
        Length = (long)length;
        if (BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(32)) != 0)
            throw new InvalidDataException("Encrypted QCOW2 images are not supported.");
        _l1Size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(36));
        _l1Offset = checked((long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(40)));
        var coverage = (long)_clusterBytes * (_clusterBytes / 8);
        if (_l1Size < (Length + coverage - 1) / coverage || _l1Size > 32 * 1024 * 1024)
            throw new InvalidDataException("Invalid QCOW2 L1 coverage.");
        AlignedPhysicalRange(_l1Offset, (long)_l1Size * 8);
        if (_version == 3)
        {
            var incompatible = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(72));
            var headerLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(100));
            if (incompatible != 0 || headerLength < 104 || headerLength > _clusterBytes)
                throw new InvalidDataException("QCOW2 dirty/corrupt, external-data, extended-L2 or other incompatible features are not supported.");
        }
    }

    public void Read(long offset, Span<byte> output)
    {
        Range(offset, output.Length, Length);
        if (_version == 0) { Physical(offset, output); return; }
        while (!output.IsEmpty)
        {
            CheckBudget();
            var cluster = offset / _clusterBytes;
            var within = (int)(offset % _clusterBytes);
            var count = Math.Min(_clusterBytes - within, output.Length);
            var l1Index = cluster / (_clusterBytes / 8);
            if (l1Index >= _l1Size) throw new InvalidDataException("QCOW2 L1 index is out of range.");
            var l1 = Entry(_l1Offset + l1Index * 8);
            if ((l1 & ~(OffsetMask | Copied)) != 0) throw new InvalidDataException("Unsupported QCOW2 L1 flags.");
            var l2Offset = (long)(l1 & OffsetMask);
            ulong l2 = 0;
            if (l2Offset != 0)
            {
                AlignedPhysicalRange(l2Offset, _clusterBytes);
                l2 = Entry(l2Offset + (cluster % (_clusterBytes / 8)) * 8);
            }
            if ((l2 & Compressed) != 0) throw new InvalidDataException("Compressed QCOW2 clusters are not supported by the bounded guest-result reader.");
            if ((l2 & ~(OffsetMask | Copied | 1UL)) != 0 || (_version == 2 && (l2 & 1) != 0))
                throw new InvalidDataException("Unsupported QCOW2 L2 flags.");
            var dataOffset = (long)(l2 & OffsetMask);
            if (dataOffset == 0 || (l2 & 1) != 0)
                output[..count].Clear();
            else
            {
                AlignedPhysicalRange(dataOffset, _clusterBytes);
                Physical(dataOffset + within, output[..count]);
            }
            offset += count;
            output = output[count..];
        }
    }

    private ulong Entry(long offset)
    {
        if (_entries.TryGetValue(offset, out var value)) return value;
        Span<byte> buffer = stackalloc byte[8];
        Physical(offset, buffer);
        value = BinaryPrimitives.ReadUInt64BigEndian(buffer);
        if (_entries.Count >= 1024) _entries.Clear();
        _entries[offset] = value;
        return value;
    }

    private void Physical(long offset, Span<byte> output)
    {
        CheckBudget();
        Range(offset, output.Length, _file.Length);
        if (BytesRead > 64 * 1024 * 1024 - output.Length)
            throw new InvalidDataException("Guest extraction exceeded its 64 MiB image-read budget.");
        _file.Position = offset;
        while (!output.IsEmpty)
        {
            CheckBudget();
            var read = _file.Read(output);
            if (read == 0) throw new EndOfStreamException("Guest image ended during extraction.");
            BytesRead += read;
            output = output[read..];
        }
    }
    private void AlignedPhysicalRange(long offset, long length)
    {
        if (offset < _clusterBytes || offset % _clusterBytes != 0)
            throw new InvalidDataException("QCOW2 allocation is not cluster-aligned.");
        Range(offset, length, _file.Length);
    }
    private void CheckBudget()
    {
        _ct.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(_started) > TimeSpan.FromSeconds(30))
            throw new TimeoutException("Guest image extraction exceeded its processing deadline.");
    }
    internal static void Range(long offset, long count, long length)
    {
        if (offset < 0 || count < 0 || offset > length || count > length - offset)
            throw new InvalidDataException("Guest image offset/length is out of bounds.");
    }
    public void Dispose() => _file.Dispose();
}
