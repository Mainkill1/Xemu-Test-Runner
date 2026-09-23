using System.Buffers.Binary;
using System.Text;

// Synthetic FATX/QCOW2 images only. Production extraction never formats, edits
// or rewrites an image. A fragmented cluster chain avoids a contiguous-file shortcut.
internal static class GuestDiskFixture
{
    public const int Size = 8 * 1024 * 1024;
    private const int Cluster = 4096;
    private const int Data = 8192;

    public static void Create(string path, string? content, bool qcow = false, bool cyclic = false)
    {
        var bytes = new byte[Size];
        Encoding.ASCII.GetBytes("FATX").CopyTo(bytes, 0);
        Put(bytes, 8, 8); Put(bytes, 12, 1);
        Fat(bytes, 1, 0xffff); Fat(bytes, 2, 0xffff);
        Entry(bytes, Data, "xemu_perf_tests", 2, 0, true);
        if (content is not null)
        {
            var payload = Encoding.UTF8.GetBytes(content);
            var clusters = Enumerable.Range(0, (payload.Length + Cluster - 1) / Cluster).Select(i => 5 + i * 2).ToArray();
            Entry(bytes, Data + Cluster, "results.txt", clusters[0], payload.Length, false);
            for (var i = 0; i < clusters.Length; i++)
            {
                payload.AsSpan(i * Cluster, Math.Min(Cluster, payload.Length - i * Cluster))
                    .CopyTo(bytes.AsSpan(Data + (clusters[i] - 1) * Cluster));
                Fat(bytes, clusters[i], cyclic ? clusters[i] : i + 1 < clusters.Length ? clusters[i + 1] : 0xffff);
            }
        }
        if (!qcow) { File.WriteAllBytes(path, bytes); return; }
        const int block = 65536;
        var image = new byte[bytes.Length + 3 * block];
        Encoding.ASCII.GetBytes("QFI\u00fb").CopyTo(image, 0);
        image[3] = 0xfb;
        Be32(image, 4, 3); Be32(image, 20, 16); Be64(image, 24, Size);
        Be32(image, 36, 1); Be64(image, 40, block); Be32(image, 96, 4); Be32(image, 100, 104);
        Be64(image, block, 2 * block);
        for (var i = 0; i < bytes.Length / block; i++) Be64(image, 2 * block + i * 8, (ulong)((3 + i) * block) | (1UL << 63));
        bytes.CopyTo(image, 3 * block);
        File.WriteAllBytes(path, image);
    }
    private static void Entry(byte[] bytes, int offset, string name, int first, int size, bool directory)
    {
        bytes[offset] = (byte)name.Length; bytes[offset + 1] = directory ? (byte)0x10 : (byte)0x20;
        Encoding.ASCII.GetBytes(name).CopyTo(bytes, offset + 2);
        Put(bytes, offset + 44, first); Put(bytes, offset + 48, size);
    }
    private static void Fat(byte[] bytes, int index, int value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4096 + index * 2, 2), (ushort)value);
    private static void Put(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset,4), value);
    private static void Be32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset,4), value);
    private static void Be64(byte[] bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(offset,8), value);
}
