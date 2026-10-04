using ImageMagick;
using NetVips;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VipsImage = NetVips.Image;

namespace TANGERINE_PhotoViewer;

internal static class ImageLoader
{
    internal sealed record LoadedImage(BitmapSource Image, bool IsGif, bool IsLarge, bool FastRegionAccess,
        int PixelWidth, int PixelHeight, long FileSize, string Format);

    public const long MaximumRegionPixels = 16_000_000;

    public static void PrepareRandomAccessCache(string sourcePath, string cachePath,
        CancellationToken token, IProgress<int> progress)
    {
        token.ThrowIfCancellationRequested();
        using var image = VipsImage.NewFromFile(sourcePath, access: NetVips.Enums.Access.Sequential);
        image.SetProgress(progress, token);
        token.ThrowIfCancellationRequested();
        image.Tiffsave(cachePath, compression: NetVips.Enums.ForeignTiffCompression.Deflate,
            level: 1, tile: true, tileWidth: 256, tileHeight: 256, pyramid: true, bigtiff: true);
        token.ThrowIfCancellationRequested();
    }

    public static LoadedImage Load(string path, long threshold, int previewWidth, int previewHeight,
        CancellationToken token, IProgress<int> progress)
    {
        token.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);
        var gif = IsGif(stream);
        var fallbackFormat = DetectFallbackFormat(stream);
        var fileSize = stream.Length;
        var large = fileSize > threshold;
        stream.Close();
        try
        {
            using var image = VipsImage.NewFromFile(path, access: NetVips.Enums.Access.Random);
            token.ThrowIfCancellationRequested();
            var width = image.Width;
            var height = image.Height;
            var format = image.GetTypeOf("vips-loader") != 0
                ? image.Get("vips-loader")?.ToString() ?? LanguageManager.Get("UnknownFormat")
                : LanguageManager.Get("UnknownFormat");
            using var preview = large
                ? VipsImage.Thumbnail(path, Math.Max(1, previewWidth), height: Math.Max(1, previewHeight))
                : VipsImage.NewFromFile(path, access: NetVips.Enums.Access.Random);
            preview.SetProgress(progress, token);
            var bitmap = FromPng(preview.WriteToBuffer(".png"));
            token.ThrowIfCancellationRequested();
            return new LoadedImage(bitmap, gif, large, large && SupportsFastRegionAccess(path, token),
                width, height, fileSize, format);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) when (!large)
        {
            token.ThrowIfCancellationRequested();
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var image = fallbackFormat == MagickFormat.Unknown
                ? new MagickImage(input)
                : new MagickImage(input, new MagickReadSettings { Format = fallbackFormat });
            token.ThrowIfCancellationRequested();
            var bitmap = FromPng(image.ToByteArray(MagickFormat.Png));
            token.ThrowIfCancellationRequested();
            return new LoadedImage(bitmap, gif, false, false, (int)image.Width, (int)image.Height,
                fileSize, image.Format.ToString());
        }
        catch (Exception ex) when (large)
        {
            token.ThrowIfCancellationRequested();
            throw new NotSupportedException(LanguageManager.Get("LargeFormatUnsupported"), ex);
        }
    }

    public static BitmapSource LoadRegion(string path, Int32Rect rect, int rotation, double displayScale,
        CancellationToken token, IProgress<int> progress)
    {
        token.ThrowIfCancellationRequested();
        using var image = OpenRegionLevel(path, rect, rotation, displayScale, out var flags,
            out var levelWidth, out var levelHeight);
        if ((flags & NetVips.Enums.ForeignFlags.PARTIAL) == 0)
            throw new NotSupportedException(LanguageManager.Get("LargeFormatUnsupported"));
        image.SetProgress(progress, token);
        var displayWidth = rotation is 90 or 270 ? image.Height : image.Width;
        var displayHeight = rotation is 90 or 270 ? image.Width : image.Height;
        var fullDisplayWidth = rotation is 90 or 270 ? levelHeight : levelWidth;
        var fullDisplayHeight = rotation is 90 or 270 ? levelWidth : levelHeight;
        var x = Math.Clamp((int)((long)rect.X * displayWidth / fullDisplayWidth), 0, displayWidth - 1);
        var y = Math.Clamp((int)((long)rect.Y * displayHeight / fullDisplayHeight), 0, displayHeight - 1);
        var right = Math.Clamp((int)Math.Ceiling((rect.X + (double)rect.Width) * displayWidth / fullDisplayWidth),
            x + 1, displayWidth);
        var bottom = Math.Clamp((int)Math.Ceiling((rect.Y + (double)rect.Height) * displayHeight / fullDisplayHeight),
            y + 1, displayHeight);
        var width = right - x;
        var height = bottom - y;
        var sourceRect = rotation switch
        {
            90 => new Int32Rect(y, image.Height - x - width, height, width),
            180 => new Int32Rect(image.Width - x - width, image.Height - y - height, width, height),
            270 => new Int32Rect(image.Width - y - height, x, height, width),
            _ => new Int32Rect(x, y, width, height)
        };
        using var crop = image.Crop(sourceRect.X, sourceRect.Y, sourceRect.Width, sourceRect.Height);
        using var oriented = rotation switch
        {
            90 => crop.Rot90(),
            180 => crop.Rot180(),
            270 => crop.Rot270(),
            _ => crop.Copy()
        };
        var effectiveScale = displayScale * fullDisplayWidth / displayWidth;
        using var tile = effectiveScale < 1 ? oriented.Resize(effectiveScale) : oriented.Copy();
        tile.SetProgress(progress, token);
        token.ThrowIfCancellationRequested();
        var bitmap = FromPng(tile.WriteToBuffer(".png"));
        token.ThrowIfCancellationRequested();
        return bitmap;
    }

    private static bool SupportsFastRegionAccess(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            using var image = OpenFastRegionImage(path, out var flags);
            token.ThrowIfCancellationRequested();
            return (flags & NetVips.Enums.ForeignFlags.PARTIAL) != 0;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private static VipsImage OpenFastRegionImage(string path, out NetVips.Enums.ForeignFlags flags)
    {
        var loader = VipsImage.FindLoad(path) ?? string.Empty;
        if (loader.Contains("Tiff", StringComparison.OrdinalIgnoreCase))
            return VipsImage.Tiffload(path, out flags, access: NetVips.Enums.Access.Random, revalidate: true);
        if (loader.Contains("Openexr", StringComparison.OrdinalIgnoreCase))
            return VipsImage.Openexrload(path, out flags, access: NetVips.Enums.Access.Random, revalidate: true);
        if (loader.Contains("Openslide", StringComparison.OrdinalIgnoreCase))
            return VipsImage.Openslideload(path, out flags, access: NetVips.Enums.Access.Random, revalidate: true);
        if (loader.Contains("Fits", StringComparison.OrdinalIgnoreCase))
            return VipsImage.Fitsload(path, out flags, access: NetVips.Enums.Access.Random, revalidate: true);
        if (loader.Contains("Jp2k", StringComparison.OrdinalIgnoreCase))
            return VipsImage.Jp2kload(path, out flags, access: NetVips.Enums.Access.Random, revalidate: true);
        if (loader.Contains("LoadVips", StringComparison.OrdinalIgnoreCase))
            return VipsImage.Vipsload(path, out flags, access: NetVips.Enums.Access.Random, revalidate: true);
        flags = NetVips.Enums.ForeignFlags.NONE;
        throw new NotSupportedException(LanguageManager.Get("LargeFormatUnsupported"));
    }

    private static VipsImage OpenRegionLevel(string path, Int32Rect rect, int rotation, double displayScale,
        out NetVips.Enums.ForeignFlags flags, out int fullWidth, out int fullHeight)
    {
        var image = OpenFastRegionImage(path, out flags);
        fullWidth = image.Width;
        fullHeight = image.Height;
        var loader = VipsImage.FindLoad(path) ?? string.Empty;
        if (!loader.Contains("Tiff", StringComparison.OrdinalIgnoreCase) ||
            (flags & NetVips.Enums.ForeignFlags.PARTIAL) == 0 || displayScale >= 1)
            return image;

        var selected = image;
        var targetWidth = Math.Max(1, Math.Ceiling(rect.Width * displayScale));
        var targetHeight = Math.Max(1, Math.Ceiling(rect.Height * displayScale));
        var rotated = rotation is 90 or 270;
        for (var level = 0; level < 16; level++)
        {
            VipsImage candidate;
            try
            {
                candidate = image.Contains("n-subifds") && Convert.ToInt32(image.Get("n-subifds")) > level
                    ? VipsImage.Tiffload(path, subifd: level, access: NetVips.Enums.Access.Random, revalidate: true)
                    : VipsImage.Tiffload(path, page: level + 1, access: NetVips.Enums.Access.Random, revalidate: true);
            }
            catch { break; }

            if (candidate.Width >= selected.Width || candidate.Height >= selected.Height ||
                candidate.Width < selected.Width * 0.35 || candidate.Height < selected.Height * 0.35 ||
                candidate.Width > selected.Width * 0.65 || candidate.Height > selected.Height * 0.65)
            {
                candidate.Dispose();
                break;
            }
            var candidateDisplayWidth = rotated ? candidate.Height : candidate.Width;
            var candidateDisplayHeight = rotated ? candidate.Width : candidate.Height;
            var widthAtLevel = rect.Width * (double)candidateDisplayWidth / (rotated ? fullHeight : fullWidth);
            var heightAtLevel = rect.Height * (double)candidateDisplayHeight / (rotated ? fullWidth : fullHeight);
            if (widthAtLevel < targetWidth || heightAtLevel < targetHeight)
            {
                candidate.Dispose();
                break;
            }
            if (!ReferenceEquals(selected, image)) selected.Dispose();
            selected = candidate;
        }
        if (!ReferenceEquals(selected, image)) image.Dispose();
        return selected;
    }

    public static async Task<BitmapSource?> LoadRegionParallelAsync(string path, Int32Rect rect, int rotation,
        double displayScale, CancellationToken token, IProgress<int> progress)
    {
        token.ThrowIfCancellationRequested();
        if ((long)rect.Width * rect.Height > MaximumRegionPixels &&
            !CanUseTiffPyramid(path, rect, rotation, displayScale)) return null;
        var workerCount = Math.Min(rect.Height, Math.Clamp(Environment.ProcessorCount, 2, 4));
        if (workerCount < 2)
            return await Task.Run(() => LoadRegion(path, rect, rotation, displayScale, token, progress), token);

        var completed = new int[workerCount];
        var tasks = new Task<BitmapSource>[workerCount];
        for (var index = 0; index < workerCount; index++)
        {
            var workerIndex = index;
            var firstRow = rect.Height * index / workerCount;
            var lastRow = rect.Height * (index + 1) / workerCount;
            var section = new Int32Rect(rect.X, rect.Y + firstRow, rect.Width, lastRow - firstRow);
            var sectionProgress = new Progress<int>(value =>
            {
                completed[workerIndex] = Math.Clamp(value, 0, 100);
                progress.Report(completed.Sum() / workerCount);
            });
            tasks[index] = Task.Run(() => LoadRegion(path, section, rotation, displayScale, token, sectionProgress), token);
        }

        var sections = await Task.WhenAll(tasks);
        token.ThrowIfCancellationRequested();
        var result = await Task.Run(() => JoinSections(sections, token), token);
        token.ThrowIfCancellationRequested();
        progress.Report(100);
        return result;
    }

    private static bool CanUseTiffPyramid(string path, Int32Rect rect, int rotation, double displayScale)
    {
        if (displayScale >= 1 || !(VipsImage.FindLoad(path) ?? string.Empty).Contains("Tiff",
                StringComparison.OrdinalIgnoreCase)) return false;
        using var image = OpenRegionLevel(path, rect, rotation, displayScale, out _, out var fullWidth,
            out var fullHeight);
        if (image.Width == fullWidth || image.Height == fullHeight) return false;
        return (long)rect.Width * image.Width / fullWidth *
            ((long)rect.Height * image.Height / fullHeight) <= MaximumRegionPixels;
    }

    private static BitmapSource JoinSections(IReadOnlyList<BitmapSource> sections, CancellationToken token)
    {
        var width = sections.Max(section => section.PixelWidth);
        var height = sections.Sum(section => section.PixelHeight);
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        var top = 0;
        foreach (var section in sections)
        {
            token.ThrowIfCancellationRequested();
            var converted = new FormatConvertedBitmap(section, PixelFormats.Bgra32, null, 0);
            var stride = checked(section.PixelWidth * 4);
            var pixels = new byte[checked(stride * section.PixelHeight)];
            converted.CopyPixels(pixels, stride, 0);
            bitmap.WritePixels(new Int32Rect(0, top, section.PixelWidth, section.PixelHeight), pixels, stride, 0);
            top += section.PixelHeight;
        }
        token.ThrowIfCancellationRequested();
        bitmap.Freeze();
        return bitmap;
    }

    public static int ExportGif(string path, string directory, CancellationToken token, IProgress<int> progress)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!IsGif(input)) throw new InvalidDataException(LanguageManager.Get("NotGif"));
        input.Position = 0;
        using var frames = new MagickImageCollection(input);
        token.ThrowIfCancellationRequested();
        frames.Coalesce();
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        for (var i = 0; i < frames.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var outputPath = System.IO.Path.Combine(directory, $"{i + 1:D5}.png");
            try
            {
                frames[i].Write(outputPath, MagickFormat.Png);
                token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) { File.Delete(outputPath); throw; }
            progress.Report((i + 1) * 100 / frames.Count);
        }
        return frames.Count;
    }

    private static BitmapSource FromPng(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    private static MagickFormat DetectFallbackFormat(Stream stream)
    {
        Span<byte> header = stackalloc byte[32];
        stream.Position = 0;
        var count = stream.Read(header);
        stream.Position = 0;
        if (count >= 4 && header[..4].SequenceEqual("8BPS"u8)) return MagickFormat.Psd;
        if (count >= 18 && header[1] == 0 && header[2] is 1 or 2 or 3 or 9 or 10 or 11 &&
            BitConverter.ToUInt16(header.Slice(12, 2)) > 0 && BitConverter.ToUInt16(header.Slice(14, 2)) > 0)
            return MagickFormat.Tga;
        return MagickFormat.Unknown;
    }
    private static bool IsGif(Stream stream)
    {
        Span<byte> signature = stackalloc byte[6];
        if (stream.Read(signature) != 6) return false;
        return signature.SequenceEqual("GIF87a"u8) || signature.SequenceEqual("GIF89a"u8);
    }

    public static bool CanDecode(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
            if (stream.Length == 0) return false;
            using var image = VipsImage.NewFromFile(path, access: NetVips.Enums.Access.Random);
            token.ThrowIfCancellationRequested();
            return image.Width > 0 && image.Height > 0;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
                var image = new MagickImageInfo(stream);
                token.ThrowIfCancellationRequested();
                return image.Width > 0 && image.Height > 0;
            }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
        }
    }
}







