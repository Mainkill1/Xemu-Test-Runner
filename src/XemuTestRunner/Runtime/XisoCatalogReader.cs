using System.Buffers.Binary;
using System.Text;

namespace XemuTestRunner.Runtime;

/// <summary>Read only D:\catalog.json from a self-contained XDVDFS image. No ISO rebuilding or mounting.</summary>
internal static class XisoCatalogReader
{
    public static byte[] Read(Stream image)
    {
        if (!image.CanSeek) throw new InvalidDataException("XISO source must be seekable.");
        var volume = ReadAt(image, 32L * 2048, 2048);
        if (!volume.AsSpan(0, 20).SequenceEqual("MICROSOFT*XBOX*MEDIA"u8) ||
            !volume.AsSpan(2028, 20).SequenceEqual("MICROSOFT*XBOX*MEDIA"u8))
            throw new InvalidDataException("Expected a self-contained XISO/XDVDFS image with its volume descriptor at sector 32.");
        var sector = BinaryPrimitives.ReadUInt32LittleEndian(volume.AsSpan(20));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(volume.AsSpan(24));
        if (size is < 14 or > 1024 * 1024) throw new InvalidDataException("XISO root directory exceeds the bounded reader contract.");
        var directory = ReadAt(image, (long)sector * 2048, checked((int)size));
        var stack = new Stack<int>(); stack.Push(0);
        var visited = new HashSet<int>();
        byte[]? catalog = null;
        while (stack.Count > 0)
        {
            var offset = stack.Pop();
            if (!visited.Add(offset) || visited.Count > 65536) throw new InvalidDataException("Invalid XISO directory graph.");
            GuestDiskReader.Range(offset, 14, directory.Length);
            var left = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(offset)) * 4;
            var right = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(offset + 2)) * 4;
            var length = directory[offset + 13];
            if (length == 0) throw new InvalidDataException("Empty XISO root entry.");
            GuestDiskReader.Range(offset + 14, length, directory.Length);
            var name = Encoding.ASCII.GetString(directory, offset + 14, length);
            if (name.Equals("catalog.json", StringComparison.OrdinalIgnoreCase))
            {
                if (catalog is not null || (directory[offset + 12] & 0x10) != 0) throw new InvalidDataException("Ambiguous XISO catalog path.");
                var fileSector = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(offset + 4));
                var fileSize = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(offset + 8));
                if (fileSize is < 2 or > 1024 * 1024) throw new InvalidDataException("XISO catalog must be 2 bytes..1 MiB.");
                catalog = ReadAt(image, (long)fileSector * 2048, (int)fileSize);
            }
            if (left != 0) stack.Push(left);
            if (right != 0) stack.Push(right);
        }
        return catalog ?? throw new InvalidDataException("XISO has no root catalog.json. Rebuild/export the matched suite artifact, not a catalog from an unrelated checkout.");
    }

    private static byte[] ReadAt(Stream file, long offset, int count)
    {
        GuestDiskReader.Range(offset, count, file.Length);
        file.Position = offset;
        var bytes = new byte[count]; file.ReadExactly(bytes); return bytes;
    }
}
