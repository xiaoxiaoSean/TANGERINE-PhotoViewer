using ImageMagick;
using NetVips;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using VipsImage = NetVips.Image;

namespace TANGERINE_PhotoViewer;

internal static class ImageLoader
{
    internal sealed record LoadedImage(BitmapSource Image, bool IsGif, bool IsLarge, int PixelWidth, int PixelHeight,
        long FileSize, string Format);

    public static LoadedImage Load(string path, long threshold, CancellationToken token, IProgress<int> progress)
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
            using var preview = large ? VipsImage.Thumbnail(path, Math.Min(1024, width)) : VipsImage.NewFromFile(path, access: NetVips.Enums.Access.Random);
            preview.SetProgress(progress, token);
            var bitmap = FromPng(preview.WriteToBuffer(".png"));
            token.ThrowIfCancellationRequested();
            return new LoadedImage(bitmap, gif, large, width, height, fileSize, format);
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
            return new LoadedImage(bitmap, gif, false, (int)image.Width, (int)image.Height, fileSize, image.Format.ToString());
        }
        catch (Exception ex) when (large)
        {
            token.ThrowIfCancellationRequested();
            throw new NotSupportedException(LanguageManager.Get("LargeFormatUnsupported"), ex);
        }
    }

    public static BitmapSource LoadRegion(string path, Int32Rect rect, int rotation, double displayScale, CancellationToken token, IProgress<int> progress)
    {
        token.ThrowIfCancellationRequested();
        using var image = VipsImage.NewFromFile(path, access: NetVips.Enums.Access.Random);
        var displayWidth = rotation is 90 or 270 ? image.Height : image.Width;
        var displayHeight = rotation is 90 or 270 ? image.Width : image.Height;
        var x = Math.Clamp(rect.X, 0, displayWidth - 1);
        var y = Math.Clamp(rect.Y, 0, displayHeight - 1);
        var width = Math.Min(rect.Width, displayWidth - x);
        var height = Math.Min(rect.Height, displayHeight - y);
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
        using var tile = displayScale < 1 ? oriented.Resize(displayScale) : oriented.Copy();
        tile.SetProgress(progress, token);
        token.ThrowIfCancellationRequested();
        var bitmap = FromPng(tile.WriteToBuffer(".png"));
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







