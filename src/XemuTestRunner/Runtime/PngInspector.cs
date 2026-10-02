using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;

namespace XemuTestRunner.Runtime;

internal static class PngInspector
{
    private static ReadOnlySpan<byte> Signature =>
        [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    internal static async Task<double> MeasureNonBlackPixelRatioAsync(
        Stream stream,
        ImageRegionDefinition? imageRegion,
        int threshold,
        CancellationToken cancellationToken)
    {
        if (threshold is < 0 or > 255)
            throw new ArgumentOutOfRangeException(
                nameof(threshold), "RGB threshold must be between 0 and 255.");
        ValidateRegion(imageRegion);
        var image = await DecodeAsync(stream, cancellationToken).ConfigureAwait(false);
        var bounds = Bounds(image, imageRegion);
        long nonBlack = 0;
        for (var y = bounds.Top; y < bounds.Bottom; y++)
        for (var x = bounds.Left; x < bounds.Right; x++)
            if (IsVisible(image, x, y, threshold)) nonBlack++;

        return nonBlack /
            (double)checked((long)(bounds.Right - bounds.Left) *
                            (bounds.Bottom - bounds.Top));
    }

    /// <summary>
    /// Produces a bounded 64-bit difference hash for a normalized image region.
    /// Each bit compares adjacent cell-average luma values in a 9x8 grid.
    /// </summary>
    internal static async Task<string> CalculateDifferenceHashAsync(
        Stream stream,
        ImageRegionDefinition? imageRegion,
        CancellationToken cancellationToken)
    {
        ValidateRegion(imageRegion);
        var image = await DecodeAsync(stream, cancellationToken).ConfigureAwait(false);
        var bounds = Bounds(image, imageRegion);
        var samples = new long[8, 9];
        for (var row = 0; row < 8; row++)
        for (var column = 0; column < 9; column++)
        {
            var left = CellEdge(bounds.Left, bounds.Right, column, 9);
            var right = CellEdge(bounds.Left, bounds.Right, column + 1, 9);
            var top = CellEdge(bounds.Top, bounds.Bottom, row, 8);
            var bottom = CellEdge(bounds.Top, bounds.Bottom, row + 1, 8);
            if (right <= left) right = Math.Min(bounds.Right, left + 1);
            if (bottom <= top) bottom = Math.Min(bounds.Bottom, top + 1);
            long total = 0;
            var count = 0;
            for (var y = top; y < bottom; y++)
            for (var x = left; x < right; x++)
            {
                total += Luma(image, x, y);
                count++;
            }
            samples[row, column] = count == 0 ? 0 : total / count;
        }

        ulong hash = 0;
        for (var row = 0; row < 8; row++)
        for (var column = 0; column < 8; column++)
        {
            hash <<= 1;
            if (samples[row, column] > samples[row, column + 1]) hash |= 1;
        }
        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static int CellEdge(int first, int last, int index, int cells) =>
        first + (int)((long)(last - first) * index / cells);

    private static void ValidateRegion(ImageRegionDefinition? imageRegion)
    {
        if (imageRegion is not { } region) return;
        if (!double.IsFinite(region.X) || !double.IsFinite(region.Y) ||
            !double.IsFinite(region.Width) || !double.IsFinite(region.Height) ||
            region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 ||
            region.X + region.Width > 1 || region.Y + region.Height > 1)
            throw new ArgumentException(
                "Image region must be a positive normalized rectangle within the image.",
                nameof(imageRegion));
    }

    private static ImageBounds Bounds(DecodedPng image, ImageRegionDefinition? imageRegion)
    {
        var region = imageRegion ?? new ImageRegionDefinition();
        var left = Math.Clamp((int)Math.Floor(region.X * image.Width), 0, image.Width - 1);
        var top = Math.Clamp((int)Math.Floor(region.Y * image.Height), 0, image.Height - 1);
        var right = Math.Clamp(
            (int)Math.Ceiling((region.X + region.Width) * image.Width), left + 1, image.Width);
        var bottom = Math.Clamp(
            (int)Math.Ceiling((region.Y + region.Height) * image.Height), top + 1, image.Height);
        return new(left, top, right, bottom);
    }

    private static bool IsVisible(DecodedPng image, int x, int y, int threshold)
    {
        var offset = y * image.Stride + x * image.BytesPerPixel;
        return image.BytesPerPixel switch
        {
            1 => image.Pixels[offset] > threshold,
            2 => image.Pixels[offset + 1] != 0 && image.Pixels[offset] > threshold,
            3 => Math.Max(image.Pixels[offset],
                     Math.Max(image.Pixels[offset + 1], image.Pixels[offset + 2])) > threshold,
            4 => image.Pixels[offset + 3] != 0 &&
                 Math.Max(image.Pixels[offset],
                     Math.Max(image.Pixels[offset + 1], image.Pixels[offset + 2])) > threshold,
            _ => false
        };
    }

    private static int Luma(DecodedPng image, int x, int y)
    {
        var offset = y * image.Stride + x * image.BytesPerPixel;
        int red, green, blue, alpha;
        switch (image.BytesPerPixel)
        {
            case 1:
                red = green = blue = image.Pixels[offset]; alpha = 255; break;
            case 2:
                red = green = blue = image.Pixels[offset]; alpha = image.Pixels[offset + 1]; break;
            case 3:
                red = image.Pixels[offset]; green = image.Pixels[offset + 1];
                blue = image.Pixels[offset + 2]; alpha = 255; break;
            case 4:
                red = image.Pixels[offset]; green = image.Pixels[offset + 1];
                blue = image.Pixels[offset + 2]; alpha = image.Pixels[offset + 3]; break;
            default: throw new InvalidOperationException("Unsupported decoded PNG format.");
        }
        return ((299 * red + 587 * green + 114 * blue) / 1000) * alpha / 255;
    }

    private static async Task<DecodedPng> DecodeAsync(
        Stream stream, CancellationToken cancellationToken)
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
                    0 => 1, 2 => 3, 4 => 2, 6 => 4,
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
        var filtered = new byte[checked((int)decodedLength)];
        await using (var inflater = new ZLibStream(
            compressed, CompressionMode.Decompress, leaveOpen: true))
        {
            await inflater.ReadExactlyAsync(filtered, cancellationToken).ConfigureAwait(false);
            var extra = new byte[1];
            if (await inflater.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0)
                throw new InvalidDataException(
                    "PNG contains more decoded data than its dimensions declare.");
        }

        var pixels = new byte[checked(stride * height)];
        var previous = new byte[stride];
        var current = new byte[stride];
        var source = 0;
        for (var row = 0; row < height; row++)
        {
            var filter = filtered[source++];
            filtered.AsSpan(source, stride).CopyTo(current);
            source += stride;
            Unfilter(current, previous, bytesPerPixel, filter);
            current.CopyTo(pixels, row * stride);
            (previous, current) = (current, previous);
        }
        return new(width, height, bytesPerPixel, stride, pixels);
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

    private sealed record DecodedPng(
        int Width, int Height, int BytesPerPixel, int Stride, byte[] Pixels);
    private readonly record struct ImageBounds(int Left, int Top, int Right, int Bottom);
}
