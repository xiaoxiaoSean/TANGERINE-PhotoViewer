using ImageMagick;
using NetVips;
using System.Globalization;
using System.IO;
using TANGERINE_PhotoViewer.DefaultApps;
using VipsImage = NetVips.Image;

namespace TANGERINE_PhotoViewer;

/// <summary>
/// A frozen source-pixel-space raster tile. Pixels are straight BGRA8, one row
/// after another; zero alpha leaves the source photograph unchanged.
/// </summary>
internal sealed record AnnotationTile(int X, int Y, int Width, int Height, byte[] BgraPixels);

/// <summary>
/// Writes raster ink at the source image's actual dimensions. The viewer's thumbnail and
/// viewport tiles never enter this pipeline, so their resolution cannot limit the output.
/// </summary>
internal static class AnnotationExport
{

    /// <summary>
    /// Returns whether the source extension has a writable image coder. This is a
    /// dialog hint; SaveAsync checks the destination again before it starts work.
    /// </summary>
    internal static bool CanSaveAsOriginal(string sourcePath)
    {
        try
        {
            var extension = Path.GetExtension(sourcePath);
            if (string.IsNullOrWhiteSpace(extension)) return false;
            return MagickFormatInfo.Create(sourcePath)?.SupportsWriting == true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Copies caller-owned tile bytes, then performs every pixel, codec, file, and
    /// verification operation on the thread pool. Progress is 0..100.
    /// A temporary sibling file prevents incomplete destination images on failure.
    /// </summary>
    internal static Task SaveAsync(string sourcePath, string destinationPath, int rotation,
        IReadOnlyList<AnnotationTile> tiles, CancellationToken token, IProgress<int> progress)
    {
        if (tiles is null || progress is null)
            throw new StageException("ANNS0001", LanguageManager.Get("AnnotationExportInvalid")); // ANNS0001
        var snapshot = tiles.Select(tile => new AnnotationTile(tile.X, tile.Y, tile.Width, tile.Height,
            tile.BgraPixels?.ToArray() ?? [])).ToArray();
        return Task.Run(() => Save(sourcePath, destinationPath, rotation, snapshot, token, progress), token);
    }

    private static void Save(string sourcePath, string destinationPath, int rotation,
        AnnotationTile[] tiles, CancellationToken token, IProgress<int> progress)
    {
        string? temporaryPath = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var source = Path.GetFullPath(sourcePath);
            var destination = Path.GetFullPath(destinationPath);
            if (!File.Exists(source) || string.Equals(source, destination, StringComparison.OrdinalIgnoreCase) ||
                rotation is not (0 or 90 or 180 or 270) || tiles.Any(tile => tile.X < 0 || tile.Y < 0 ||
                tile.Width <= 0 || tile.Height <= 0 || (long)tile.Width * tile.Height * 4 != tile.BgraPixels.Length))
                throw new StageException("ANNS0001", LanguageManager.Get("AnnotationExportInvalid")); // ANNS0001

            var extension = Path.GetExtension(destination).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(extension) ||
                MagickFormatInfo.Create(destination)?.SupportsWriting != true)
                throw new StageException("ANNS0002", LanguageManager.Get("AnnotationExportUnsupported")); // ANNS0002

            temporaryPath = Path.Combine(Path.GetDirectoryName(destination)!, "." +
                Path.GetFileNameWithoutExtension(destination) + "." + Guid.NewGuid().ToString("N") + extension);
            progress.Report(0);
            token.ThrowIfCancellationRequested();

            // Animated and multipage containers need their complete frame collection.
            // Loading them as a single VipsImage would silently discard later pages.
            if (IsMultipage(source))
                SaveMultipage(source, temporaryPath, rotation, tiles, token, progress);
            else
            {
                try
                {
                    SaveWithVips(source, temporaryPath, rotation, tiles, token, progress);
                }
                catch (VipsException) when (CanUseMagickFallback(source, destination))
                {
                    TryDelete(temporaryPath);
                    SaveWithMagick(source, temporaryPath, rotation, tiles, token, progress);
                }
            }

            token.ThrowIfCancellationRequested();
            VerifyDimensions(source, temporaryPath, rotation);
            token.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destination, true);
            temporaryPath = null;
            progress.Report(100);
        }
        catch (OperationCanceledException) { throw; }
        catch (StageException) { throw; }
        catch (Exception error)
        {
            throw new StageException("ANNS0003", LanguageManager.Get("AnnotationExportFailed"), error); // ANNS0003
        }
        finally { if (temporaryPath is not null) TryDelete(temporaryPath); }
    }

    private static bool CanUseMagickFallback(string source, string destination)
    {
        try { return new FileInfo(source).Length < 512L * 1024 * 1024 &&
            MagickFormatInfo.Create(destination)?.SupportsWriting == true; }
        catch { return false; }
    }

    private static bool IsMultipage(string source)
    {
        var extension = Path.GetExtension(source).ToLowerInvariant();
        // A Vips header read is cheap even for a huge TIFF. Loading the entire
        // MagickImageCollection here would eagerly decode every page before the
        // streaming single-page path has a chance to run.
        if (extension is ".gif" or ".apng" or ".pdf" or ".psd" or ".tif" or ".tiff" or
            ".webp" or ".heic" or ".heif" or ".avif")
        {
            try
            {
                using var image = VipsImage.NewFromFile(source, access: NetVips.Enums.Access.Sequential);
                if (image.Contains("n-pages")) return Convert.ToInt32(image.Get("n-pages"), CultureInfo.InvariantCulture) > 1;
            }
            catch (VipsException) { /* The header reader below covers Magick-only inputs. */ }
            return MagickImageInfo.ReadCollection(source).Take(2).Count() > 1;
        }
        return false;
    }

    private static void SaveWithVips(string sourcePath, string outputPath, int rotation,
        AnnotationTile[] tiles, CancellationToken token, IProgress<int> progress)
    {
        NetVips.NetVips.Concurrency = Math.Clamp(Environment.ProcessorCount, 2, 4);
        using var original = VipsImage.NewFromFile(sourcePath, access: NetVips.Enums.Access.Sequential);
        token.ThrowIfCancellationRequested();
        ValidateTileBounds(tiles, original.Width, original.Height);
        var intermediates = new List<VipsImage>();
        var overlays = new List<VipsImage>();
        try
        {
            VipsImage current = original;
            for (var index = 0; index < tiles.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var tile = tiles[index];
                var overlay = CreateVipsOverlay(tile);
                overlays.Add(overlay);
                var next = current.Composite2(overlay, NetVips.Enums.BlendMode.Over, tile.X, tile.Y);
                intermediates.Add(next);
                current = next;
                if (index % 32 == 0 || index + 1 == tiles.Length)
                    progress.Report(15 + (index + 1) * 25 / Math.Max(1, tiles.Length));
            }
            token.ThrowIfCancellationRequested();
            using var oriented = rotation switch
            {
                90 => current.Rot90(),
                180 => current.Rot180(),
                270 => current.Rot270(),
                _ => current.Copy()
            };
            oriented.SetProgress(new Progress<int>(percent => progress.Report(40 + Math.Clamp(percent, 0, 100) * 55 / 100)), token);
            var extension = Path.GetExtension(outputPath).ToLowerInvariant();
            // The supported Vips savers stream directly from the lazy source graph.
            // The `keep` option copies EXIF, IPTC, XMP, and ICC data where the coder
            // supports it. JPEG quality is read from the source header when possible.
            var options = new VOption { ["keep"] = NetVips.Enums.ForeignKeep.All };
            if (extension is ".jpg" or ".jpeg" or ".jpe" or ".webp" or ".heic" or ".heif" or ".avif" or ".jxl")
            {
                try
                {
                    var quality = new MagickImageInfo(sourcePath).Quality;
                    if (quality is > 0 and <= 100) options["Q"] = (int)quality;
                }
                catch { /* The original decoder remains authoritative for pixel data. */ }
            }
            if (extension == ".png" && original.Contains("png-compression"))
            {
                try { options["compression"] = Convert.ToInt32(original.Get("png-compression"), CultureInfo.InvariantCulture); }
                catch { /* Not all PNG loaders expose compression metadata. */ }
            }
            if (extension is not (".jpg" or ".jpeg" or ".jpe" or ".png" or ".tif" or ".tiff" or
                ".webp" or ".heic" or ".heif" or ".avif" or ".jxl" or ".jp2" or ".j2k" or ".gif" or
                ".bmp" or ".ppm" or ".pgm" or ".pbm" or ".fits" or ".vips"))
                throw new VipsException("No streaming saver is configured for this extension.");
            oriented.WriteToFile(outputPath, options);
            token.ThrowIfCancellationRequested();
            progress.Report(95);
        }
        finally
        {
            for (var i = intermediates.Count - 1; i >= 0; i--) intermediates[i].Dispose();
            for (var i = overlays.Count - 1; i >= 0; i--) overlays[i].Dispose();
        }
    }

    private static void ValidateTileBounds(AnnotationTile[] tiles, int width, int height)
    {
        if (tiles.Any(tile => (long)tile.X + tile.Width > width || (long)tile.Y + tile.Height > height))
            throw new StageException("ANNS0001", LanguageManager.Get("AnnotationExportInvalid")); // ANNS0001
    }

    private static VipsImage CreateVipsOverlay(AnnotationTile tile)
    {
        var rgba = new byte[tile.BgraPixels.Length];
        for (var offset = 0; offset < rgba.Length; offset += 4)
        {
            rgba[offset] = tile.BgraPixels[offset + 2];
            rgba[offset + 1] = tile.BgraPixels[offset + 1];
            rgba[offset + 2] = tile.BgraPixels[offset];
            rgba[offset + 3] = tile.BgraPixels[offset + 3];
        }
        using var raw = VipsImage.NewFromMemoryCopy<byte>(rgba, tile.Width, tile.Height, 4,
            NetVips.Enums.BandFormat.Uchar);
        return raw.Copy(interpretation: NetVips.Enums.Interpretation.Srgb);
    }

    private static void SaveMultipage(string sourcePath, string outputPath, int rotation,
        AnnotationTile[] tiles, CancellationToken token, IProgress<int> progress)
    {
        using var frames = new MagickImageCollection(sourcePath);
        if (frames.Count == 0)
            throw new StageException("ANNS0001", LanguageManager.Get("AnnotationExportInvalid")); // ANNS0001
        // GIF frames can be sparse rectangles relative to their page canvas.
        // Coalescing reconstructs those pixels before annotating the displayed
        // first frame, and preserves the frame count, delays, and loop metadata.
        if (Path.GetExtension(sourcePath).Equals(".gif", StringComparison.OrdinalIgnoreCase))
            frames.Coalesce();
        for (var i = 0; i < frames.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (i == 0) CompositeTiles(frames[i], tiles, token, progress);
            if (rotation != 0) frames[i].Rotate(rotation);
            progress.Report(15 + (i + 1) * 30 / frames.Count);
        }
        token.ThrowIfCancellationRequested();
        frames.Write(outputPath);
        progress.Report(95);
    }

    private static void SaveWithMagick(string sourcePath, string outputPath, int rotation,
        AnnotationTile[] tiles, CancellationToken token, IProgress<int> progress)
    {
        using var image = new MagickImage(sourcePath);
        token.ThrowIfCancellationRequested();
        CompositeTiles(image, tiles, token, progress);
        if (rotation != 0) image.Rotate(rotation);
        token.ThrowIfCancellationRequested();
        image.Write(outputPath);
        progress.Report(95);
    }

    private static void CompositeTiles(IMagickImage<byte> image, AnnotationTile[] tiles,
        CancellationToken token, IProgress<int> progress)
    {
        ValidateTileBounds(tiles, (int)image.Width, (int)image.Height);
        for (var i = 0; i < tiles.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var tile = tiles[i];
            var rgba = new byte[tile.BgraPixels.Length];
            for (var offset = 0; offset < rgba.Length; offset += 4)
            {
                rgba[offset] = tile.BgraPixels[offset + 2];
                rgba[offset + 1] = tile.BgraPixels[offset + 1];
                rgba[offset + 2] = tile.BgraPixels[offset];
                rgba[offset + 3] = tile.BgraPixels[offset + 3];
            }
            using var overlay = new MagickImage();
            overlay.ReadPixels(rgba, new PixelReadSettings((uint)tile.Width, (uint)tile.Height,
                StorageType.Char, "RGBA"));
            image.Composite(overlay, tile.X, tile.Y, CompositeOperator.Over);
            progress.Report(15 * (i + 1) / Math.Max(1, tiles.Length));
        }
    }

    private static void VerifyDimensions(string sourcePath, string outputPath, int rotation)
    {
        try
        {
            var source = new MagickImageInfo(sourcePath);
            var output = new MagickImageInfo(outputPath);
            var expectedWidth = rotation is 90 or 270 ? source.Height : source.Width;
            var expectedHeight = rotation is 90 or 270 ? source.Width : source.Height;
            if (output.Width != expectedWidth || output.Height != expectedHeight)
                throw new StageException("ANNS0004", LanguageManager.Get("AnnotationExportVerificationFailed")); // ANNS0004
            if (IsMultipage(sourcePath) &&
                MagickImageInfo.ReadCollection(sourcePath).Count() != MagickImageInfo.ReadCollection(outputPath).Count())
                throw new StageException("ANNS0004", LanguageManager.Get("AnnotationExportVerificationFailed")); // ANNS0004
        }
        catch (StageException) { throw; }
        catch (Exception error)
        {
            throw new StageException("ANNS0004", LanguageManager.Get("AnnotationExportVerificationFailed"), error); // ANNS0004
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
