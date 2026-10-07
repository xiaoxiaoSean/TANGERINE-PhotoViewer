using ImageMagick;
using NetVips;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TANGERINE_PhotoViewer.DefaultApps;
using VipsImage = NetVips.Image;

namespace TANGERINE_PhotoViewer;

internal static class ImageLoader
{
    private const long MaximumOrdinaryPixels = 32_000_000;
    private const ulong MaximumMagickMemoryBytes = 256UL * 1024 * 1024;
    private const ulong MaximumMagickDiskBytes = 64UL * 1024 * 1024 * 1024;

    internal sealed record LoadedImage(BitmapSource Image, bool IsGif, bool IsLarge,
        int PixelWidth, int PixelHeight, long FileSize, string Format);

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
        if (fallbackFormat == MagickFormat.Psd)
        {
            PsdCompositeReader.Info info;
            try { info = PsdCompositeReader.Inspect(path, token); }
            catch (OperationCanceledException) { throw; }
            catch (NotSupportedException)
            {
                // Other decoders may support a PSD mode or composite layout
                // that the bounded reader cannot interpret.
                if (!large) return LoadOrdinaryCore(path, fileSize, gif, fallbackFormat, token, progress);
                using var fallbackStream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var fallbackInfo = new MagickImageInfo(fallbackStream,
                    new MagickReadSettings { Format = MagickFormat.Psd });
                var fallbackPreview = LoadMagickPreview(path, previewWidth, previewHeight, token);
                return new LoadedImage(fallbackPreview, false, true,
                    checked((int)fallbackInfo.Width), checked((int)fallbackInfo.Height),
                    fileSize, fallbackInfo.Format.ToString());
            }
            large |= (long)info.Width * info.Height > MaximumOrdinaryPixels;
            if (!large) return LoadOrdinaryCore(path, fileSize, gif, fallbackFormat, token, progress);
            BitmapSource preview;
            try
            {
                preview = PsdCompositeReader.LoadPreview(path, info, previewWidth,
                    previewHeight, token, progress);
            }
            catch (NotSupportedException)
            {
                token.ThrowIfCancellationRequested();
                preview = LoadMagickPreview(path, previewWidth, previewHeight, token);
            }
            token.ThrowIfCancellationRequested();
            return new LoadedImage(preview, false, true, info.Width, info.Height,
                fileSize, info.Version == 2 ? "PSB" : "PSD");
        }
        var ordinaryDecodeStarted = false;
        try
        {
            using var image = VipsImage.NewFromFile(path, memory: false,
                access: NetVips.Enums.Access.Sequential);
            token.ThrowIfCancellationRequested();
            var width = image.Width;
            var height = image.Height;
            large |= (long)width * height > MaximumOrdinaryPixels;
            if (!large)
            {
                ordinaryDecodeStarted = true;
                return LoadOrdinaryCore(path, fileSize, gif, fallbackFormat, token, progress);
            }
            var format = image.GetTypeOf("vips-loader") != 0
                ? image.Get("vips-loader")?.ToString() ?? LanguageManager.Get("UnknownFormat")
                : LanguageManager.Get("UnknownFormat");
            var bitmap = LoadSequentialPreview(image, previewWidth, previewHeight,
                token, progress);
            token.ThrowIfCancellationRequested();
            return new LoadedImage(bitmap, gif, true,
                width, height, fileSize, format);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            token.ThrowIfCancellationRequested();
            if (ordinaryDecodeStarted) throw;
            if (!large)
            {
                // A small compressed file can still describe a gigantic
                // raster. Probe dimensions before asking Magick to decode it.
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
                var info = fallbackFormat == MagickFormat.Unknown
                    ? new MagickImageInfo(input)
                    : new MagickImageInfo(input,
                        new MagickReadSettings { Format = fallbackFormat });
                token.ThrowIfCancellationRequested();
                if ((long)info.Width * info.Height <= MaximumOrdinaryPixels)
                    return LoadOrdinaryCore(path, fileSize, gif, fallbackFormat, token, progress);
            }
            try
            {
                using var infoStream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var info = fallbackFormat == MagickFormat.Unknown
                    ? new MagickImageInfo(infoStream)
                    : new MagickImageInfo(infoStream,
                        new MagickReadSettings { Format = fallbackFormat });
                token.ThrowIfCancellationRequested();
                if (info.Width == 0 || info.Height == 0)
                    throw new InvalidDataException(LanguageManager.Get("UnsupportedImage"));
                var preview = LoadMagickPreview(path, previewWidth, previewHeight, token);
                return new LoadedImage(preview, gif, true, checked((int)info.Width),
                    checked((int)info.Height), fileSize, info.Format.ToString());
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception fallbackError)
            {
                throw new StageException("IMGL0002",
                    string.Format(LanguageManager.Get("ImageDecodeFailed"),
                        $"{error.Message}; {fallbackError.Message}"), fallbackError);
            }
        }
    }

    private static LoadedImage LoadOrdinaryCore(string path, long fileSize, bool gif,
        MagickFormat fallbackFormat, CancellationToken token, IProgress<int> progress)
    {
        token.ThrowIfCancellationRequested();
        NetVips.NetVips.Concurrency = Math.Clamp(Environment.ProcessorCount, 2, 4);
        try
        {
            using var image = VipsImage.NewFromFile(path, access: NetVips.Enums.Access.Random);
            image.SetProgress(progress, token);
            var format = image.GetTypeOf("vips-loader") != 0
                ? image.Get("vips-loader")?.ToString() ?? LanguageManager.Get("UnknownFormat")
                : LanguageManager.Get("UnknownFormat");
            var bitmap = FromPng(image.WriteToBuffer(".png"));
            token.ThrowIfCancellationRequested();
            return new LoadedImage(bitmap, gif, false, image.Width, image.Height, fileSize, format);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                LimitMagickPixelCache();
                // Magick identifies ordinary formats from file content. The
                // explicit PSD/TGA hints also come from their file signatures.
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var image = fallbackFormat == MagickFormat.Unknown
                    ? new MagickImage(input)
                    : new MagickImage(input, new MagickReadSettings { Format = fallbackFormat });
                token.ThrowIfCancellationRequested();
                var bitmap = FromPng(image.ToByteArray(MagickFormat.Png));
                token.ThrowIfCancellationRequested();
                return new LoadedImage(bitmap, gif, false, checked((int)image.Width),
                    checked((int)image.Height), fileSize, image.Format.ToString());
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                token.ThrowIfCancellationRequested();
                throw new StageException("IMGL0001",
                    string.Format(LanguageManager.Get("ImageDecodeFailed"), error.Message), error);
            }
        }
    }

    public static BitmapSource LoadPreview(string path, int targetWidth, int targetHeight,
        CancellationToken token, IProgress<int> progress)
    {
        token.ThrowIfCancellationRequested();
        if (PsdCompositeReader.HasSignature(path))
        {
            try
            {
                var info = PsdCompositeReader.Inspect(path, token);
                return PsdCompositeReader.LoadPreview(path, info, targetWidth,
                    targetHeight, token, progress);
            }
            catch (OperationCanceledException) { throw; }
            catch (NotSupportedException)
            {
                token.ThrowIfCancellationRequested();
                return LoadMagickPreview(path, targetWidth, targetHeight, token);
            }
        }
        NetVips.NetVips.Concurrency = Math.Clamp(Environment.ProcessorCount, 2, 4);
        try
        {
            using var image = VipsImage.NewFromFile(path, memory: false,
                access: NetVips.Enums.Access.Sequential);
            return LoadSequentialPreview(image, targetWidth, targetHeight, token, progress);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            token.ThrowIfCancellationRequested();
            return LoadMagickPreview(path, targetWidth, targetHeight, token);
        }
    }

    private static BitmapSource LoadSequentialPreview(VipsImage image, int targetWidth,
        int targetHeight, CancellationToken token, IProgress<int> progress)
    {
        token.ThrowIfCancellationRequested();
        var ratio = Math.Min(1d, Math.Min((double)Math.Max(1, targetWidth) / image.Width,
            (double)Math.Max(1, targetHeight) / image.Height));
        // For a scanline source, resizing directly into a bounded preview
        // avoids the full-image random-access mode of a generic thumbnail.
        using var preview = ratio < 1 ? image.Resize(ratio) : image.Copy();
        preview.SetProgress(progress, token);
        var bitmap = FromPng(preview.WriteToBuffer(".png"));
        token.ThrowIfCancellationRequested();
        return bitmap;
    }

    public static BitmapSource LoadRegion(string path, Int32Rect rect, int rotation, double displayScale,
        CancellationToken token, IProgress<int> progress)
    {
        token.ThrowIfCancellationRequested();
        if (PsdCompositeReader.HasSignature(path))
        {
            var info = PsdCompositeReader.Inspect(path, token);
            return PsdCompositeReader.LoadRegion(path, info, rect, rotation,
                displayScale, token, progress);
        }
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
        var targetWidth = Math.Max(1, Math.Ceiling(rect.Width * displayScale * 1.5));
        var targetHeight = Math.Max(1, Math.Ceiling(rect.Height * displayScale * 1.5));
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
        NetVips.NetVips.Concurrency = Math.Clamp(Environment.ProcessorCount, 2, 4);
        return await Task.Run(() =>
        {
            if (PsdCompositeReader.HasSignature(path))
            {
                try { return LoadRegion(path, rect, rotation, displayScale, token, progress); }
                catch (OperationCanceledException) { throw; }
                catch (NotSupportedException)
                {
                    token.ThrowIfCancellationRequested();
                    return LoadMagickRegion(path, rect, rotation, displayScale, token);
                }
                catch (InvalidDataException)
                {
                    token.ThrowIfCancellationRequested();
                    return LoadMagickRegion(path, rect, rotation, displayScale, token);
                }
            }
            if (SupportsFastRegionAccess(path, token))
            {
                try { return LoadRegion(path, rect, rotation, displayScale, token, progress); }
                catch (OperationCanceledException) { throw; }
                catch { token.ThrowIfCancellationRequested(); }
            }
            try { return LoadSequentialRegion(path, rect, rotation, displayScale, token, progress); }
            catch (OperationCanceledException) { throw; }
            catch
            {
                token.ThrowIfCancellationRequested();
                return LoadMagickRegion(path, rect, rotation, displayScale, token);
            }
        }, token);
    }

    private static void LimitMagickPixelCache()
    {
        // Resource limits are process-wide, so the bounded policy is retained
        // for every later Magick decode, including simultaneous operations.
        ResourceLimits.Memory = Math.Min(ResourceLimits.Memory, MaximumMagickMemoryBytes);
        ResourceLimits.Area = Math.Min(ResourceLimits.Area, MaximumMagickMemoryBytes / 4);
        ResourceLimits.Disk = Math.Min(ResourceLimits.Disk, MaximumMagickDiskBytes);
        ResourceLimits.Thread = Math.Min(ResourceLimits.Thread,
            (uint)Math.Clamp(Environment.ProcessorCount, 2, 4));
    }

    private static BitmapSource LoadMagickPreview(string path, int targetWidth,
        int targetHeight, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        LimitMagickPixelCache();
        using var image = OpenMagickFromContent(path);
        token.ThrowIfCancellationRequested();
        var ratio = Math.Min(1d, Math.Min((double)Math.Max(1, targetWidth) / image.Width,
            (double)Math.Max(1, targetHeight) / image.Height));
        if (ratio < 1)
            image.Resize(Math.Max(1u, (uint)Math.Round(image.Width * ratio)),
                Math.Max(1u, (uint)Math.Round(image.Height * ratio)), FilterType.Lanczos);
        token.ThrowIfCancellationRequested();
        return FromPng(image.ToByteArray(MagickFormat.Png));
    }

    private static BitmapSource LoadMagickRegion(string path, Int32Rect rect, int rotation,
        double displayScale, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        LimitMagickPixelCache();
        using var image = OpenMagickFromContent(path);
        token.ThrowIfCancellationRequested();
        var sourceRect = rotation switch
        {
            90 => new Int32Rect(rect.Y, checked((int)image.Height) - rect.X - rect.Width,
                rect.Height, rect.Width),
            180 => new Int32Rect(checked((int)image.Width) - rect.X - rect.Width,
                checked((int)image.Height) - rect.Y - rect.Height, rect.Width, rect.Height),
            270 => new Int32Rect(checked((int)image.Width) - rect.Y - rect.Height,
                rect.X, rect.Height, rect.Width),
            _ => rect
        };
        image.Crop(new MagickGeometry(sourceRect.X, sourceRect.Y,
            (uint)sourceRect.Width, (uint)sourceRect.Height));
        image.ResetPage();
        token.ThrowIfCancellationRequested();
        if (displayScale < 1)
            image.Resize(Math.Max(1u, (uint)Math.Round(image.Width * displayScale)),
                Math.Max(1u, (uint)Math.Round(image.Height * displayScale)), FilterType.Lanczos);
        if (rotation != 0) image.Rotate(rotation);
        token.ThrowIfCancellationRequested();
        return FromPng(image.ToByteArray(MagickFormat.Png));
    }

    private static MagickImage OpenMagickFromContent(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var format = DetectFallbackFormat(stream);
        return format == MagickFormat.Unknown
            ? new MagickImage(stream)
            : new MagickImage(stream, new MagickReadSettings { Format = format });
    }

    private static BitmapSource LoadSequentialRegion(string path, Int32Rect rect, int rotation,
        double displayScale, CancellationToken token, IProgress<int> progress)
    {
        token.ThrowIfCancellationRequested();
        if (!double.IsFinite(displayScale) || displayScale <= 0)
            throw new ArgumentOutOfRangeException(nameof(displayScale));

        // Scanline decoders can skip earlier rows and stop after the visible
        // crop. The output raster is bounded to the visible display area.
        using var image = VipsImage.NewFromFile(path, memory: false,
            access: NetVips.Enums.Access.Sequential);
        token.ThrowIfCancellationRequested();
        var rotated = rotation is 90 or 270;
        var displayWidth = rotated ? image.Height : image.Width;
        var displayHeight = rotated ? image.Width : image.Height;
        if (rect.Width <= 0 || rect.Height <= 0 || rect.X < 0 || rect.Y < 0 ||
            (long)rect.X + rect.Width > displayWidth || (long)rect.Y + rect.Height > displayHeight)
            throw new ArgumentOutOfRangeException(nameof(rect));

        // Rotating before cropping would force a sequential source into
        // random access and could materialize the complete image.
        var sourceRect = rotation switch
        {
            90 => new Int32Rect(rect.Y, image.Height - rect.X - rect.Width,
                rect.Height, rect.Width),
            180 => new Int32Rect(image.Width - rect.X - rect.Width,
                image.Height - rect.Y - rect.Height, rect.Width, rect.Height),
            270 => new Int32Rect(image.Width - rect.Y - rect.Height, rect.X,
                rect.Height, rect.Width),
            _ => rect
        };
        using var crop = image.Crop(sourceRect.X, sourceRect.Y,
            sourceRect.Width, sourceRect.Height);
        using var scaled = displayScale < 1 ? crop.Resize(displayScale) : crop.Copy();
        using var oriented = rotation switch
        {
            90 => scaled.Rot90(),
            180 => scaled.Rot180(),
            270 => scaled.Rot270(),
            _ => scaled.Copy()
        };
        oriented.SetProgress(progress, token);
        token.ThrowIfCancellationRequested();
        var bitmap = FromPng(oriented.WriteToBuffer(".png"));
        token.ThrowIfCancellationRequested();
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

    internal static MagickFormat DetectFallbackFormat(Stream stream)
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
            // Keep directory navigation aligned with Open: libvips may not
            // recognize a valid PSB, but the merged-composite reader does.
            if (PsdCompositeReader.HasSignature(path))
            {
                try
                {
                    var info = PsdCompositeReader.Inspect(path, token);
                    return info.Width > 0 && info.Height > 0;
                }
                catch (NotSupportedException)
                {
                    // A different decoder may understand this Photoshop
                    // variant, so continue with the general header probes.
                }
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
            if (stream.Length == 0) return false;
            using var image = VipsImage.NewFromFile(path, memory: false,
                access: NetVips.Enums.Access.Sequential);
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
                var format = DetectFallbackFormat(stream);
                var image = format == MagickFormat.Unknown
                    ? new MagickImageInfo(stream)
                    : new MagickImageInfo(stream, new MagickReadSettings { Format = format });
                token.ThrowIfCancellationRequested();
                return image.Width > 0 && image.Height > 0;
            }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
        }
    }
}







