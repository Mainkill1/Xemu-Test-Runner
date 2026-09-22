using System.Buffers.Binary;
using System.IO.Compression;

namespace XemuTestRunner.Runtime;

internal static class PngInspector
{
    private static ReadOnlySpan<byte> Signature =>
        [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    internal static async Task<double> MeasureNonBlackPixelRatioAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var signature = new byte[8];
        await stream.ReadExactlyAsync(signature, cancellationToken).ConfigureAwait(false);
        if (!signature.AsSpan().SequenceEqual(Signature))
            throw new InvalidDataException("Image-content checks require a PNG artifact.");

        int width = 0, height = 0, bytesPerPixel = 0;
        var sawHeader = false;
        var sawEnd = false;
        using var compressed = new MemoryStream();
        var chunkHeader = new byte[8];
        var crc = new byte[4];
        while (!sawEnd)
        {
            await stream.ReadExactlyAsync(chunkHeader, cancellationToken).ConfigureAwait(false);
            var chunkLength = BinaryPrimitives.ReadUInt32BigEndian(chunkHeader);
            if (chunkLength > ArtifactInspector.MaximumImageBytes)
                throw new InvalidDataException("PNG chunk exceeds the 64 MiB image evaluation limit.");

            var chunkType = BinaryPrimitives.ReadUInt32BigEndian(chunkHeader.AsSpan(4));
            var data = new byte[checked((int)chunkLength)];
            await stream.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
            await stream.ReadExactlyAsync(crc, cancellationToken).ConfigureAwait(false);

            if (chunkType == 0x49484452)
            {
                if (sawHeader || data.Length != 13)
                    throw new InvalidDataException("PNG has an invalid IHDR chunk.");
                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data));
                height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4)));
                var bitDepth = data[8];
                var colorType = data[9];
                if (width <= 0 || height <= 0 || bitDepth != 8 || data[10] != 0 ||
                    data[11] != 0 || data[12] != 0)
                    throw new InvalidDataException(
                        "Image-content checks support non-interlaced 8-bit PNG artifacts.");
                bytesPerPixel = colorType switch
                {
                    0 => 1,
                    2 => 3,
                    4 => 2,
                    6 => 4,
                    _ => throw new InvalidDataException(
                        $"Image-content checks do not support PNG color type {colorType}.")
                };
                sawHeader = true;
            }
            else if (chunkType == 0x49444154)
            {
                if (!sawHeader)
                    throw new InvalidDataException("PNG IDAT appeared before IHDR.");
                if (compressed.Length + data.Length > ArtifactInspector.MaximumImageBytes)
                    throw new InvalidDataException("PNG data exceeds the 64 MiB image evaluation limit.");
                await compressed.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            }
            else if (chunkType == 0x49454e44)
            {
                if (data.Length != 0)
                    throw new InvalidDataException("PNG has an invalid IEND chunk.");
                sawEnd = true;
            }
        }

        if (!sawHeader || compressed.Length == 0)
            throw new InvalidDataException("PNG is missing required image data.");

        var stride = checked(width * bytesPerPixel);
        var decodedLength = checked((long)(stride + 1) * height);
        if (decodedLength > ArtifactInspector.MaximumDecodedImageBytes)
            throw new InvalidDataException("Decoded PNG exceeds the 256 MiB image evaluation limit.");

        compressed.Position = 0;
        var decoded = new byte[checked((int)decodedLength)];
        await using (var inflater = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true))
        {
            await inflater.ReadExactlyAsync(decoded, cancellationToken).ConfigureAwait(false);
            var extra = new byte[1];
            if (await inflater.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0)
                throw new InvalidDataException("PNG contains more decoded data than its dimensions declare.");
        }

        var previous = new byte[stride];
        var current = new byte[stride];
        long nonBlack = 0;
        var offset = 0;
        for (var row = 0; row < height; row++)
        {
            var filter = decoded[offset++];
            decoded.AsSpan(offset, stride).CopyTo(current);
            offset += stride;
            Unfilter(current, previous, bytesPerPixel, filter);

            for (var x = 0; x < stride; x += bytesPerPixel)
            {
                var visible = bytesPerPixel switch
                {
                    1 => current[x] != 0,
                    2 => current[x + 1] != 0 && current[x] != 0,
                    3 => (current[x] | current[x + 1] | current[x + 2]) != 0,
                    4 => current[x + 3] != 0 &&
                        (current[x] | current[x + 1] | current[x + 2]) != 0,
                    _ => false
                };
                if (visible) nonBlack++;
            }

            (previous, current) = (current, previous);
        }

        return nonBlack / (double)checked((long)width * height);
    }

    private static void Unfilter(byte[] row, byte[] previous, int bytesPerPixel, byte filter)
    {
        for (var index = 0; index < row.Length; index++)
        {
            var left = index >= bytesPerPixel ? row[index - bytesPerPixel] : 0;
            var up = previous[index];
            var upperLeft = index >= bytesPerPixel ? previous[index - bytesPerPixel] : 0;
            var predictor = filter switch
            {
                0 => 0,
                1 => left,
                2 => up,
                3 => (left + up) / 2,
                4 => Paeth(left, up, upperLeft),
                _ => throw new InvalidDataException($"PNG uses unknown row filter {filter}.")
            };
            row[index] = unchecked((byte)(row[index] + predictor));
        }
    }

    private static int Paeth(int left, int up, int upperLeft)
    {
        var estimate = left + up - upperLeft;
        var leftDistance = Math.Abs(estimate - left);
        var upDistance = Math.Abs(estimate - up);
        var upperLeftDistance = Math.Abs(estimate - upperLeft);
        return leftDistance <= upDistance && leftDistance <= upperLeftDistance
            ? left
            : upDistance <= upperLeftDistance ? up : upperLeft;
    }
}
