using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TANGERINE_PhotoViewer;

// The Photoshop merged image is stored after the document's layers. Raw and
// PackBits composites have independently addressable rows. ZIP composites
// require a bounded sequential scan. Neither path materializes a full bitmap.
internal static class PsdCompositeReader
{
    internal readonly record struct Info(int Width, int Height, int Channels, int Depth,
        int ColorMode, int Compression, int Version, bool HasAlpha, long PixelDataOffset,
        long[]? RowOffsets, int[]? RowLengths);

    private const int MaximumOutputPixels = 16_000_000;
    private const int MaximumRowIndexBytes = 64 * 1024 * 1024;
    private const int MaximumSourceRowBytes = 64 * 1024 * 1024;

    public static bool HasSignature(string path)
    {
        using var stream = OpenStream(path);
        Span<byte> signature = stackalloc byte[4];
        return stream.Read(signature) == 4 && signature.SequenceEqual("8BPS"u8);
    }

    public static Info Inspect(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stream = OpenStream(path);
        Span<byte> header = stackalloc byte[26];
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("8BPS"u8))
            throw new InvalidDataException(LanguageManager.Get("UnsupportedImage"));
        var version = BinaryPrimitives.ReadUInt16BigEndian(header[4..6]);
        if (version is not (1 or 2))
            throw new InvalidDataException(LanguageManager.Get("UnsupportedImage"));
        var channels = BinaryPrimitives.ReadUInt16BigEndian(header[12..14]);
        var height = BinaryPrimitives.ReadInt32BigEndian(header[14..18]);
        var width = BinaryPrimitives.ReadInt32BigEndian(header[18..22]);
        var depth = BinaryPrimitives.ReadUInt16BigEndian(header[22..24]);
        var mode = BinaryPrimitives.ReadUInt16BigEndian(header[24..26]);
        var colorChannels = mode == 3 ? 3 : mode == 4 ? 4 : 1;
        if (width <= 0 || height <= 0 || channels < colorChannels || channels > 56 ||
            (version == 1 && (width > 30_000 || height > 30_000)) ||
            (version == 2 && (width > 300_000 || height > 300_000)) ||
            depth is not (8 or 16 or 32) || mode is not (1 or 3 or 4))
            throw new NotSupportedException(LanguageManager.Get("UnsupportedImage"));

        SkipSection(stream, false);
        SkipSection(stream, false);
        var layerSectionLength = version == 2 ? ReadUInt64(stream) : ReadUInt32(stream);
        var layerSectionEnd = checked(stream.Position + checked((long)layerSectionLength));
        if (layerSectionEnd > stream.Length) throw new EndOfStreamException();
        var hasAlpha = false;
        if (layerSectionLength >= (version == 2 ? 10UL : 6UL))
        {
            var layerInfoLength = version == 2 ? ReadUInt64(stream) : ReadUInt32(stream);
            if (layerInfoLength >= 2 &&
                checked(stream.Position + checked((long)layerInfoLength)) <= layerSectionEnd)
                hasAlpha = unchecked((short)ReadUInt16(stream)) < 0;
        }
        stream.Position = layerSectionEnd;
        var compression = ReadUInt16(stream);
        if (compression is not (0 or 1 or 2 or 3))
            throw new NotSupportedException(LanguageManager.Get("UnsupportedImage"));

        var dataOffset = stream.Position;
        long[]? offsets = null;
        int[]? lengths = null;
        if (compression == 1)
        {
            var neededChannels = Math.Min(channels, colorChannels + (hasAlpha ? 1 : 0));
            var neededRows = checked((long)neededChannels * height);
            // The row-count table is kept only for channels that contribute to
            // the visible composite. It is small compared with pixel data, but
            // the cap prevents a hostile header from allocating gigabytes.
            if ((long)neededRows * (sizeof(long) + sizeof(int)) > MaximumRowIndexBytes)
                throw new NotSupportedException(LanguageManager.Get("UnsupportedImage"));
            offsets = new long[checked((int)neededRows)];
            lengths = new int[offsets.Length];
            var countBytes = version == 2 ? 4 : 2;
            var rowCount = checked((long)channels * height);
            dataOffset = checked(stream.Position + rowCount * countBytes);
            if (dataOffset > stream.Length) throw new EndOfStreamException();
            long position = dataOffset;
            for (long i = 0; i < rowCount; i++)
            {
                if ((i & 0x3fff) == 0) token.ThrowIfCancellationRequested();
                var length = version == 2 ? ReadUInt32(stream) : ReadUInt16(stream);
                if (i < offsets.Length)
                {
                    offsets[i] = position;
                    lengths[i] = checked((int)length);
                }
                position = checked(position + length);
            }
            if (position > stream.Length) throw new EndOfStreamException();
        }
        else if (compression == 0)
        {
            var bytesPerRow = checked((long)width * depth / 8);
            if (checked(dataOffset + bytesPerRow * height * channels) > stream.Length)
                throw new EndOfStreamException();
        }
        return new Info(width, height, channels, depth, mode, compression, version,
            hasAlpha, dataOffset, offsets, lengths);
    }

    public static BitmapSource LoadPreview(string path, Info info, int targetWidth,
        int targetHeight, CancellationToken token, IProgress<int>? progress = null)
    {
        var ratio = Math.Min(1d, Math.Min((double)targetWidth / info.Width,
            (double)targetHeight / info.Height));
        var width = Math.Max(1, (int)Math.Round(info.Width * ratio));
        var height = Math.Max(1, (int)Math.Round(info.Height * ratio));
        return Render(path, info, new Int32Rect(0, 0, info.Width, info.Height), 0,
            width, height, token, progress);
    }

    public static BitmapSource LoadRegion(string path, Info info, Int32Rect viewport,
        int rotation, double displayScale, CancellationToken token,
        IProgress<int>? progress = null)
    {
        var width = (int)Math.Clamp(Math.Ceiling(viewport.Width * displayScale), 1,
            Math.Min(int.MaxValue / 4, MaximumOutputPixels));
        var height = (int)Math.Clamp(Math.Ceiling(viewport.Height * displayScale), 1,
            Math.Min(int.MaxValue / 4, MaximumOutputPixels));
        return Render(path, info, viewport, rotation, width, height, token, progress);
    }

    /// <summary>
    /// Decodes the merged Photoshop image at its full pixel dimensions into an
    /// interleaved RGBA file for libvips rawload. The output is written a row at
    /// a time. ZIP channel planes update the same disk file in separate passes,
    /// so even a PSB with billions of pixels does not require a full-size managed
    /// bitmap or a second uncompressed channel cache in memory or on disk.
    /// The caller owns and deletes the completed file after the lazy save graph
    /// has finished reading it.
    /// </summary>
    public static void WriteInterleavedRgba(string sourcePath, Info info,
        string outputPath, CancellationToken token, IProgress<int>? progress = null)
    {
        token.ThrowIfCancellationRequested();
        var rowSize = checked(info.Width * info.Depth / 8);
        if (rowSize > MaximumSourceRowBytes)
            throw new NotSupportedException(LanguageManager.Get("UnsupportedImage"));
        var stride = checked(info.Width * 4);
        var byteLength = checked((long)stride * info.Height);
        var colorChannels = info.ColorMode == 3 ? 3 : info.ColorMode == 4 ? 4 : 1;
        var neededChannels = colorChannels + (info.HasAlpha ? 1 : 0);
        if (neededChannels > info.Channels)
            throw new InvalidDataException(LanguageManager.Get("UnsupportedImage"));
        try
        {
            using var output = new FileStream(outputPath, FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
            if (info.Compression is 2 or 3)
                WriteZipInterleaved(sourcePath, info, output, rowSize, stride,
                    byteLength, neededChannels, colorChannels, token, progress);
            else
                WriteIndexedInterleaved(sourcePath, info, output, rowSize, stride,
                    neededChannels, token, progress);
            token.ThrowIfCancellationRequested();
            output.Flush(flushToDisk: true);
            if (output.Length != byteLength)
                throw new EndOfStreamException();
        }
        catch
        {
            try { File.Delete(outputPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static void WriteIndexedInterleaved(string sourcePath, Info info,
        FileStream output, int rowSize, int stride, int neededChannels,
        CancellationToken token, IProgress<int>? progress)
    {
        using var source = OpenStream(sourcePath);
        var rows = new byte[neededChannels][];
        var rgba = ArrayPool<byte>.Shared.Rent(stride);
        try
        {
            for (var channel = 0; channel < neededChannels; channel++)
                rows[channel] = ArrayPool<byte>.Shared.Rent(rowSize);
            Span<byte> bgraPixel = stackalloc byte[4];
            for (var y = 0; y < info.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (var channel = 0; channel < neededChannels; channel++)
                    ReadRow(source, info, channel, y, rows[channel].AsSpan(0, rowSize), token);
                for (var x = 0; x < info.Width; x++)
                {
                    WritePixel(info, rows, x, bgraPixel);
                    var target = x * 4;
                    rgba[target] = bgraPixel[2];
                    rgba[target + 1] = bgraPixel[1];
                    rgba[target + 2] = bgraPixel[0];
                    rgba[target + 3] = bgraPixel[3];
                }
                output.Write(rgba.AsSpan(0, stride));
                if ((y & 31) == 0 || y + 1 == info.Height)
                    progress?.Report((int)((long)(y + 1) * 100 / info.Height));
            }
        }
        finally
        {
            foreach (var row in rows)
                if (row is not null) ArrayPool<byte>.Shared.Return(row);
            ArrayPool<byte>.Shared.Return(rgba);
        }
    }

    private static void WriteZipInterleaved(string sourcePath, Info info,
        FileStream output, int rowSize, int stride, long byteLength,
        int neededChannels, int colorChannels, CancellationToken token,
        IProgress<int>? progress)
    {
        // One zlib stream contains consecutive channel planes. Each plane is
        // decoded in source order and merged into the same RGBA row on disk.
        // CMYK needs four color samples plus alpha. The fourth component is
        // kept in an auxiliary row plane so it is not mistaken for alpha.
        string? blackPath = null;
        FileStream? blackPlane = null;
        if (info.ColorMode == 4 && info.HasAlpha)
        {
            blackPath = output.Name + ".black";
            blackPlane = new FileStream(blackPath, FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
            blackPlane.SetLength(checked((long)info.Width * info.Height));
        }
        output.SetLength(byteLength);
        var sourceRow = ArrayPool<byte>.Shared.Rent(rowSize);
        var rgba = ArrayPool<byte>.Shared.Rent(stride);
        var blackRow = info.ColorMode == 4 ? ArrayPool<byte>.Shared.Rent(info.Width) : null;
        var predicted = info.Compression == 3 && info.Depth == 32
            ? ArrayPool<byte>.Shared.Rent(rowSize) : null;
        try
        {
            using var source = OpenStream(sourcePath);
            source.Position = info.PixelDataOffset;
            using var zip = new ZLibStream(source, CompressionMode.Decompress);
            for (var channel = 0; channel < neededChannels; channel++)
                for (var y = 0; y < info.Height; y++)
                {
                    token.ThrowIfCancellationRequested();
                    zip.ReadExactly(sourceRow.AsSpan(0, rowSize));
                    if (info.Compression == 3)
                        UndoPrediction(sourceRow.AsSpan(0, rowSize), predicted,
                            info.Width, info.Depth);
                    var offset = checked((long)y * stride);
                    if (channel == 0)
                        Array.Clear(rgba, 0, stride);
                    else
                        ReadExactlyAt(output.SafeFileHandle, rgba.AsSpan(0, stride), offset);
                    for (var x = 0; x < info.Width; x++)
                    {
                        var sample = ReadSample(sourceRow, x, info.Depth);
                        var target = x * 4;
                        if (info.ColorMode == 1 && channel == 0)
                            rgba[target] = rgba[target + 1] = rgba[target + 2] = (byte)sample;
                        else if (info.ColorMode == 4 && channel == 0)
                            rgba[target] = (byte)sample;
                        else if (info.ColorMode == 4 && channel == 1)
                            rgba[target + 1] = (byte)sample;
                        else if (info.ColorMode == 4 && channel == 2)
                            rgba[target + 2] = (byte)sample;
                        else if (info.ColorMode == 4 && channel == 3)
                        {
                            blackRow![x] = (byte)sample;
                        }
                        else if (channel < colorChannels)
                            rgba[target + channel] = (byte)sample;
                        if (info.HasAlpha && channel == colorChannels)
                            rgba[target + 3] = (byte)sample;
                        else if (!info.HasAlpha && channel == 0)
                            rgba[target + 3] = 255;
                    }
                    if (info.ColorMode == 4 && channel == 3 && info.HasAlpha)
                        RandomAccess.Write(blackPlane!.SafeFileHandle,
                            blackRow!.AsSpan(0, info.Width), checked((long)y * info.Width));
                    if (info.ColorMode == 4 && channel == neededChannels - 1)
                    {
                        if (info.HasAlpha)
                            ReadExactlyAt(blackPlane!.SafeFileHandle,
                                blackRow!.AsSpan(0, info.Width), checked((long)y * info.Width));
                        for (var x = 0; x < info.Width; x++)
                        {
                            var target = x * 4;
                            var black = blackRow![x];
                            for (var component = 0; component < 3; component++)
                                rgba[target + component] = (byte)((rgba[target + component] *
                                    black + 127) / 255);
                        }
                    }
                    RandomAccess.Write(output.SafeFileHandle, rgba.AsSpan(0, stride), offset);
                    if ((y & 31) == 0 || y + 1 == info.Height)
                        progress?.Report((int)(((long)channel * info.Height + y + 1) * 100 /
                            ((long)neededChannels * info.Height)));
                }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sourceRow);
            ArrayPool<byte>.Shared.Return(rgba);
            if (blackRow is not null) ArrayPool<byte>.Shared.Return(blackRow);
            if (predicted is not null) ArrayPool<byte>.Shared.Return(predicted);
            blackPlane?.Dispose();
            if (blackPath is not null)
            {
                try { File.Delete(blackPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void ReadExactlyAt(Microsoft.Win32.SafeHandles.SafeFileHandle file,
        Span<byte> buffer, long offset)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = RandomAccess.Read(file, buffer[read..], offset + read);
            if (count == 0) throw new EndOfStreamException();
            read += count;
        }
    }

    private static BitmapSource Render(string path, Info info, Int32Rect rect, int rotation,
        int requestedWidth, int requestedHeight, CancellationToken token, IProgress<int>? progress)
    {
        token.ThrowIfCancellationRequested();
        var fullWidth = rotation is 90 or 270 ? info.Height : info.Width;
        var fullHeight = rotation is 90 or 270 ? info.Width : info.Height;
        if (rect.X < 0 || rect.Y < 0 || rect.Width <= 0 || rect.Height <= 0 ||
            (long)rect.X + rect.Width > fullWidth || (long)rect.Y + rect.Height > fullHeight)
            throw new ArgumentOutOfRangeException(nameof(rect));
        var outputScale = Math.Min(1d,
            Math.Sqrt((double)MaximumOutputPixels / requestedWidth / requestedHeight));
        var width = Math.Max(1, (int)Math.Floor(requestedWidth * outputScale));
        var height = Math.Max(1, (int)Math.Floor(requestedHeight * outputScale));
        if (info.Compression is 2 or 3)
            return RenderZip(path, info, rect, rotation, width, height, token, progress);
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        using var stream = OpenStream(path);
        var rowSize = checked(info.Width * info.Depth / 8);
        if (rowSize > MaximumSourceRowBytes)
            throw new NotSupportedException(LanguageManager.Get("UnsupportedImage"));
        var colorChannels = info.ColorMode == 3 ? 3 : info.ColorMode == 4 ? 4 : 1;
        var rowBuffers = new byte[colorChannels + (info.HasAlpha ? 1 : 0)][];
        var lastRow = new int[rowBuffers.Length];
        Array.Fill(lastRow, -1);
        try
        {
            for (var channel = 0; channel < rowBuffers.Length; channel++)
                rowBuffers[channel] = ArrayPool<byte>.Shared.Rent(rowSize);
            // A quarter-turn maps each output column to one source scan line.
            var iterations = rotation is 90 or 270 ? width : height;
            var innerCount = rotation is 90 or 270 ? height : width;
            var outerExtent = rotation is 90 or 270 ? rect.Width : rect.Height;
            var innerExtent = rotation is 90 or 270 ? rect.Height : rect.Width;
            var outerSamples = SampleCount(outerExtent, iterations);
            var innerSamples = SampleCount(innerExtent, innerCount);
            var sampleCount = outerSamples * innerSamples;
            var sums = new int[checked(innerCount * 4)];
            Span<byte> samplePixel = stackalloc byte[4];
            for (var outer = 0; outer < iterations; outer++)
            {
                token.ThrowIfCancellationRequested();
                Array.Clear(sums);
                for (var outerSub = 0; outerSub < outerSamples; outerSub++)
                {
                    var displayCoordinate = rotation is 90 or 270
                        ? Sample(rect.X, rect.Width, outer, width, outerSub, outerSamples)
                        : Sample(rect.Y, rect.Height, outer, height, outerSub, outerSamples);
                    var sourceRow = rotation switch
                    {
                        90 => info.Height - 1 - displayCoordinate,
                        270 => displayCoordinate,
                        180 => info.Height - 1 - displayCoordinate,
                        _ => displayCoordinate
                    };
                    for (var channel = 0; channel < rowBuffers.Length; channel++)
                    {
                        if (lastRow[channel] == sourceRow) continue;
                        ReadRow(stream, info, channel, sourceRow,
                            rowBuffers[channel].AsSpan(0, rowSize), token);
                        lastRow[channel] = sourceRow;
                    }
                    for (var inner = 0; inner < innerCount; inner++)
                    {
                        for (var innerSub = 0; innerSub < innerSamples; innerSub++)
                        {
                            var otherCoordinate = rotation is 90 or 270
                                ? Sample(rect.Y, rect.Height, inner, height, innerSub, innerSamples)
                                : Sample(rect.X, rect.Width, inner, width, innerSub, innerSamples);
                            var sourceColumn = rotation switch
                            {
                                90 => otherCoordinate,
                                270 => info.Width - 1 - otherCoordinate,
                                180 => info.Width - 1 - otherCoordinate,
                                _ => otherCoordinate
                            };
                            WritePixel(info, rowBuffers, sourceColumn, samplePixel);
                            var sumIndex = inner * 4;
                            for (var component = 0; component < 4; component++)
                                sums[sumIndex + component] += samplePixel[component];
                        }
                    }
                }
                for (var inner = 0; inner < innerCount; inner++)
                {
                    var outputX = rotation is 90 or 270 ? outer : inner;
                    var outputY = rotation is 90 or 270 ? inner : outer;
                    var target = pixels.AsSpan(checked(outputY * stride + outputX * 4), 4);
                    var sumIndex = inner * 4;
                    for (var component = 0; component < 4; component++)
                        target[component] = (byte)((sums[sumIndex + component] + sampleCount / 2) / sampleCount);
                }
                if ((outer & 15) == 0 || outer + 1 == iterations)
                    progress?.Report((outer + 1) * 100 / iterations);
            }
        }
        finally
        {
            foreach (var row in rowBuffers)
                if (row is not null) ArrayPool<byte>.Shared.Return(row);
        }
        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32,
            null, pixels, stride);
        result.Freeze();
        return result;
    }

    private static BitmapSource RenderZip(string path, Info info, Int32Rect rect, int rotation,
        int width, int height, CancellationToken token, IProgress<int>? progress)
    {
        // ZIP composites are one continuous zlib stream across all channel
        // planes. Earlier scan lines must be inflated, but never retained.
        var rowSize = checked(info.Width * info.Depth / 8);
        if (rowSize > MaximumSourceRowBytes)
            throw new NotSupportedException(LanguageManager.Get("UnsupportedImage"));
        var colorChannels = info.ColorMode == 3 ? 3 : info.ColorMode == 4 ? 4 : 1;
        var neededChannels = colorChannels + (info.HasAlpha ? 1 : 0);
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        var sums = new int[checked(width * height)];
        var rotated = rotation is 90 or 270;
        var outerCount = rotated ? width : height;
        var innerCount = rotated ? height : width;
        var outerStart = rotated ? rect.X : rect.Y;
        var outerExtent = rotated ? rect.Width : rect.Height;
        var innerStart = rotated ? rect.Y : rect.X;
        var innerExtent = rotated ? rect.Height : rect.Width;
        var outerSamples = SampleCount(outerExtent, outerCount);
        var innerSamples = SampleCount(innerExtent, innerCount);
        var sampleCount = outerSamples * innerSamples;
        var flattenedOuterCount = checked(outerCount * outerSamples);
        var row = ArrayPool<byte>.Shared.Rent(rowSize);
        var predicted = info.Compression == 3 && info.Depth == 32
            ? ArrayPool<byte>.Shared.Rent(rowSize) : null;
        try
        {
            using var stream = OpenStream(path);
            stream.Position = info.PixelDataOffset;
            using var zip = new ZLibStream(stream, CompressionMode.Decompress);
            for (var channel = 0; channel < neededChannels; channel++)
            {
                token.ThrowIfCancellationRequested();
                Array.Clear(sums);
                for (var sourceRow = 0; sourceRow < info.Height; sourceRow++)
                {
                    token.ThrowIfCancellationRequested();
                    zip.ReadExactly(row.AsSpan(0, rowSize));
                    if (info.Compression == 3)
                        UndoPrediction(row.AsSpan(0, rowSize), predicted, info.Width, info.Depth);
                    var displayRow = rotation is 90 or 180 ? info.Height - 1 - sourceRow : sourceRow;
                    var first = LowerSample(outerStart, outerExtent, outerCount,
                        outerSamples, flattenedOuterCount, displayRow);
                    var last = LowerSample(outerStart, outerExtent, outerCount,
                        outerSamples, flattenedOuterCount, displayRow + 1);
                    for (var flattened = first; flattened < last; flattened++)
                    {
                        var outer = flattened / outerSamples;
                        for (var inner = 0; inner < innerCount; inner++)
                        {
                            var pixelIndex = rotated
                                ? checked(inner * width + outer)
                                : checked(outer * width + inner);
                            var sum = sums[pixelIndex];
                            for (var sub = 0; sub < innerSamples; sub++)
                            {
                                var displayColumn = Sample(innerStart, innerExtent,
                                    inner, innerCount, sub, innerSamples);
                                var sourceColumn = rotation is 180 or 270
                                    ? info.Width - 1 - displayColumn : displayColumn;
                                sum += ReadSample(row, sourceColumn, info.Depth);
                            }
                            sums[pixelIndex] = sum;
                        }
                    }
                    if ((sourceRow & 31) == 0 || sourceRow + 1 == info.Height)
                        progress?.Report((int)(((long)channel * info.Height + sourceRow + 1) *
                            100 / ((long)neededChannels * info.Height)));
                }
                for (var index = 0; index < sums.Length; index++)
                {
                    var component = (byte)((sums[index] + sampleCount / 2) / sampleCount);
                    var target = index * 4;
                    if (info.ColorMode == 1)
                        pixels[target] = pixels[target + 1] = pixels[target + 2] = component;
                    else if (info.ColorMode == 4)
                    {
                        if (channel < 3) pixels[target + 2 - channel] = component;
                        else if (channel == 3)
                        {
                            pixels[target] = (byte)((pixels[target] * component + 127) / 255);
                            pixels[target + 1] = (byte)((pixels[target + 1] * component + 127) / 255);
                            pixels[target + 2] = (byte)((pixels[target + 2] * component + 127) / 255);
                        }
                    }
                    else if (channel < 3)
                        pixels[target + 2 - channel] = component;
                    if (channel == colorChannels && info.HasAlpha)
                        pixels[target + 3] = component;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(row);
            if (predicted is not null) ArrayPool<byte>.Shared.Return(predicted);
        }
        if (!info.HasAlpha)
            for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32,
            null, pixels, stride);
        result.Freeze();
        return result;
    }

    private static int LowerSample(int start, int extent, int outputCount, int subCount,
        int flattenedCount, int coordinate)
    {
        var low = 0;
        var high = flattenedCount;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            var sample = Sample(start, extent, mid / subCount, outputCount,
                mid % subCount, subCount);
            if (sample < coordinate) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    private static int ReadSample(byte[] row, int x, int depth)
    {
        var offset = checked(x * depth / 8);
        if (depth == 8) return row[offset];
        if (depth == 16)
            return (BinaryPrimitives.ReadUInt16BigEndian(row.AsSpan(offset, 2)) + 128) / 257;
        var value = BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32BigEndian(row.AsSpan(offset, 4)));
        return double.IsFinite(value)
            ? (int)Math.Round(Math.Clamp((double)value, 0, 1) * 255) : 0;
    }

    private static void UndoPrediction(Span<byte> row, byte[]? temporary, int width, int depth)
    {
        if (depth == 8)
        {
            for (var x = 1; x < row.Length; x++)
                row[x] = unchecked((byte)(row[x] + row[x - 1]));
        }
        else if (depth == 16)
        {
            var previous = BinaryPrimitives.ReadUInt16BigEndian(row[..2]);
            for (var x = 1; x < width; x++)
            {
                var index = x * 2;
                previous = unchecked((ushort)(previous +
                    BinaryPrimitives.ReadUInt16BigEndian(row.Slice(index, 2))));
                BinaryPrimitives.WriteUInt16BigEndian(row.Slice(index, 2), previous);
            }
        }
        else
        {
            for (var x = 1; x < row.Length; x++)
                row[x] = unchecked((byte)(row[x] + row[x - 1]));
            var destination = temporary!.AsSpan(0, row.Length);
            for (var x = 0; x < width; x++)
                for (var plane = 0; plane < 4; plane++)
                    destination[x * 4 + plane] = row[plane * width + x];
            destination.CopyTo(row);
        }
    }

    private static void ReadRow(FileStream stream, Info info, int channel, int row,
        Span<byte> destination, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (info.Compression == 0)
        {
            var offset = checked(info.PixelDataOffset +
                ((long)channel * info.Height + row) * destination.Length);
            stream.Position = offset;
            stream.ReadExactly(destination);
            return;
        }
        var index = checked(channel * info.Height + row);
        var length = info.RowLengths![index];
        var maxEncoded = checked(destination.Length * 2 + 1024);
        if (length < 0 || length > maxEncoded)
            throw new InvalidDataException(LanguageManager.Get("UnsupportedImage"));
        var rented = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
        try
        {
            stream.Position = info.RowOffsets![index];
            stream.ReadExactly(rented.AsSpan(0, length));
            DecodePackBits(rented.AsSpan(0, length), destination);
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    private static void DecodePackBits(ReadOnlySpan<byte> encoded, Span<byte> decoded)
    {
        var input = 0;
        var output = 0;
        while (input < encoded.Length && output < decoded.Length)
        {
            var instruction = unchecked((sbyte)encoded[input++]);
            if (instruction >= 0)
            {
                var count = instruction + 1;
                if (input + count > encoded.Length || output + count > decoded.Length)
                    throw new InvalidDataException(LanguageManager.Get("UnsupportedImage"));
                encoded.Slice(input, count).CopyTo(decoded.Slice(output, count));
                input += count;
                output += count;
            }
            else if (instruction != -128)
            {
                var count = 1 - instruction;
                if (input >= encoded.Length || output + count > decoded.Length)
                    throw new InvalidDataException(LanguageManager.Get("UnsupportedImage"));
                decoded.Slice(output, count).Fill(encoded[input++]);
                output += count;
            }
        }
        if (output != decoded.Length)
            throw new InvalidDataException(LanguageManager.Get("UnsupportedImage"));
    }

    private static void WritePixel(Info info, byte[][] rows, int x, Span<byte> pixel)
    {
        var offset = checked(x * info.Depth / 8);
        byte Read(int channel)
        {
            if (channel >= rows.Length) return 255;
            var row = rows[channel];
            if (info.Depth == 8) return row[offset];
            if (info.Depth == 16) return (byte)((BinaryPrimitives.ReadUInt16BigEndian(
                row.AsSpan(offset, 2)) + 128) / 257);
            var bits = BinaryPrimitives.ReadInt32BigEndian(row.AsSpan(offset, 4));
            var value = BitConverter.Int32BitsToSingle(bits);
            return double.IsFinite(value)
                ? (byte)Math.Round(Math.Clamp((double)value, 0, 1) * 255) : (byte)0;
        }
        byte r;
        byte g;
        byte b;
        if (info.ColorMode == 1)
            r = g = b = Read(0);
        else if (info.ColorMode == 4)
        {
            // Photoshop stores inverted CMYK values in the merged composite.
            var c = Read(0) / 255d;
            var m = Read(1) / 255d;
            var y = Read(2) / 255d;
            var k = Read(3) / 255d;
            r = (byte)Math.Round(255 * c * k);
            g = (byte)Math.Round(255 * m * k);
            b = (byte)Math.Round(255 * y * k);
        }
        else
        {
            r = Read(0);
            g = Read(1);
            b = Read(2);
        }
        var alphaChannel = info.ColorMode == 3 ? 3 : info.ColorMode == 4 ? 4 : 1;
        pixel[0] = b;
        pixel[1] = g;
        pixel[2] = r;
        pixel[3] = info.HasAlpha ? Read(alphaChannel) : (byte)255;
    }

    private static int SampleCount(int sourceExtent, int outputExtent)
    {
        var ratio = (double)sourceExtent / outputExtent;
        return ratio >= 4 ? 4 : ratio >= 2 ? 2 : 1;
    }

    private static int Sample(int start, int extent, int index, int outputExtent,
        int subIndex, int subCount) =>
        start + Math.Min(extent - 1,
            (int)((index + (subIndex + 0.5) / subCount) * extent / outputExtent));

    private static void SkipSection(FileStream stream, bool largeLength)
    {
        var length = largeLength ? ReadUInt64(stream) : ReadUInt32(stream);
        var end = checked(stream.Position + checked((long)length));
        if (end > stream.Length) throw new EndOfStreamException();
        stream.Position = end;
    }

    private static ushort ReadUInt16(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[2];
        stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadUInt16BigEndian(bytes);
    }

    private static uint ReadUInt32(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[4];
        stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    private static ulong ReadUInt64(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[8];
        stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadUInt64BigEndian(bytes);
    }

    private static FileStream OpenStream(string path) => new(path, FileMode.Open,
        FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);
}
