using ImageMagick;
using NetVips;
using System.Globalization;
using System.IO;
using System.Text;
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
    /// Takes a structural snapshot of the caller's already-frozen tiles, then performs
    /// every pixel, codec, file, and verification operation on the thread pool.
    /// The caller must not change tile pixel arrays while the save is running.
    /// Avoiding a second copy is important when a large image contains many ink tiles.
    /// Progress is 0..100.
    /// A temporary sibling file prevents incomplete destination images on failure.
    /// </summary>
    internal static Task SaveAsync(string sourcePath, string destinationPath, int rotation,
        IReadOnlyList<AnnotationTile> tiles, CancellationToken token, IProgress<int> progress)
        => SaveAsync(sourcePath, destinationPath, rotation, tiles, null, token, progress);

    /// <summary>
    /// The optional text source is consumed on the worker only, one original-pixel
    /// region at a time. The caller owns and disposes it after awaiting this task.
    /// </summary>
    internal static Task SaveAsync(string sourcePath, string destinationPath, int rotation,
        IReadOnlyList<AnnotationTile> tiles, TextAnnotationSource? textSource,
        CancellationToken token, IProgress<int> progress)
    {
        if (tiles is null || progress is null)
            throw new StageException("ANNS0001", LanguageManager.Get("AnnotationExportInvalid")); // ANNS0001
        var snapshot = tiles.ToArray();
        return Task.Run(() => Save(sourcePath, destinationPath, rotation, snapshot,
            textSource, token, progress), token);
    }

    private static void Save(string sourcePath, string destinationPath, int rotation,
        AnnotationTile[] tiles, TextAnnotationSource? textSource,
        CancellationToken token, IProgress<int> progress)
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
            // Signal that the background worker has passed path and output
            // format validation, before the source header probe starts.
            progress.Report(1);
            token.ThrowIfCancellationRequested();

            // Large multipage TIFFs must never enter MagickImageCollection: it
            // eagerly decodes every page and can exhaust native process memory.
            // libvips holds uniform pages as one lazy vertical image instead.
            var multipage = IsMultipage(source);
            progress.Report(5);
            if (multipage)
            {
                var isTiff = Path.GetExtension(source).Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(source).Equals(".tiff", StringComparison.OrdinalIgnoreCase);
                if (isTiff)
                {
                    SaveWithVips(source, temporaryPath, rotation, tiles, textSource,
                        token, progress, multipageTiff: true);
                }
                else
                {
                    if (!CanUseMagickFallback(source, destination, multipage: true))
                        throw new StageException("ANNS0002", LanguageManager.Get("AnnotationExportUnsupported")); // ANNS0002
                    var multipageTiles = textSource is null ? tiles :
                        CombineSmallTextTiles(tiles, textSource, source, token, progress);
                    SaveMultipage(source, temporaryPath, rotation, multipageTiles, token, progress);
                }
            }
            else
            {
                try
                {
                    SaveWithVips(source, temporaryPath, rotation, tiles, textSource, token, progress);
                }
                catch (VipsException) when (CanUseMagickFallback(source, destination, multipage: false))
                {
                    // Some coders can read the source in libvips but cannot
                    // write its original format after an RGBA text overlay.
                    // Use the bounded decoder for small single-frame files;
                    // never let this fallback eagerly load a large image.
                    TryDelete(temporaryPath);
                    var fallbackTiles = textSource is { HasVisibleText: true }
                        ? CombineSmallTextTiles(tiles, textSource, source, token, progress)
                        : tiles;
                    SaveWithMagick(source, temporaryPath, rotation, fallbackTiles, token, progress);
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
            // Preserve the native coder's actual reason in the visible error.
            // A generic ANNS0003 alone cannot distinguish a format limitation,
            // a write failure, or an invalid overlay supplied by the encoder.
            throw new StageException("ANNS0003",
                $"{LanguageManager.Get("AnnotationExportFailed")} {error.Message}", error); // ANNS0003
        }
        finally { if (temporaryPath is not null) TryDelete(temporaryPath); }
    }

    private static bool CanUseMagickFallback(string source, string destination, bool multipage)
    {
        try
        {
            // A highly compressed source may be small on disk while its decoded
            // pixels require many gigabytes. Magick eagerly holds the full image.
            if (new FileInfo(source).Length >= 256L * 1024 * 1024 ||
                MagickFormatInfo.Create(destination)?.SupportsWriting != true)
                return false;
            const ulong decodedLimit = 256UL * 1024 * 1024;
            if (!multipage)
            {
                var info = new MagickImageInfo(source);
                return (ulong)info.Width * info.Height * 4 < decodedLimit;
            }
            // ReadCollection returns frame headers without a decoded image
            // collection. Bound both frame count and total decoded pixels before
            // allowing the older Magick path for small GIFs and similar files.
            ulong decodedBytes = 0;
            var frameCount = 0;
            foreach (var frame in MagickImageInfo.ReadCollection(source))
            {
                if (++frameCount > 256) return false;
                var frameBytes = (ulong)frame.Width * frame.Height * 4;
                if (frameBytes >= decodedLimit - decodedBytes) return false;
                decodedBytes += frameBytes;
            }
            return frameCount > 0;
        }
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
            catch (VipsException) { return IsMultipageFallback(source); }
            // libvips read the header successfully. A missing page count means
            // one page; enumerating the file again is unnecessary and can hold
            // a small export before its first output write.
            return false;
        }
        return false;
    }

    private static bool IsMultipageFallback(string source)
    {
        // A native header probe for very large inputs must not traverse an
        // unbounded frame collection if libvips could not read the header.
        if (new FileInfo(source).Length >= 256L * 1024 * 1024)
            throw new StageException("ANNS0002", LanguageManager.Get("AnnotationExportUnsupported")); // ANNS0002
        return MagickImageInfo.ReadCollection(source).Take(2).Count() > 1;
    }

    private static AnnotationTile[] CombineSmallTextTiles(AnnotationTile[] brushTiles,
        TextAnnotationSource textSource, string sourcePath, CancellationToken token,
        IProgress<int> progress)
    {
        // This fallback is only entered after the whole multipage source was
        // bounded below 256 MiB decoded. Text is sampled into source-pixel tiles
        // with a separate 256 MiB cap, preserving small animated GIF support.
        const long textLimit = 256L * 1024 * 1024;
        var info = new MagickImageInfo(sourcePath);
        var width = checked((int)info.Width);
        var height = checked((int)info.Height);
        var results = new List<AnnotationTile>(brushTiles);
        var occupied = new HashSet<(int X, int Y)>();
        foreach (var bound in textSource.SourceBounds)
        {
            var x0 = Math.Clamp((int)Math.Floor(bound.Left), 0, width);
            var y0 = Math.Clamp((int)Math.Floor(bound.Top), 0, height);
            var x1 = Math.Clamp((int)Math.Ceiling(bound.Right), 0, width);
            var y1 = Math.Clamp((int)Math.Ceiling(bound.Bottom), 0, height);
            if (x0 >= x1 || y0 >= y1) continue;
            for (var tileY = y0 / 256; tileY <= (y1 - 1) / 256; tileY++)
                for (var tileX = x0 / 256; tileX <= (x1 - 1) / 256; tileX++)
                    occupied.Add((tileX, tileY));
        }
        if ((long)occupied.Count * 256 * 256 * 4 >= textLimit)
            throw new StageException("ANNS0002", LanguageManager.Get("AnnotationExportUnsupported")); // ANNS0002
        var index = 0;
        foreach (var (tileX, tileY) in occupied.OrderBy(key => key.Y).ThenBy(key => key.X))
        {
            token.ThrowIfCancellationRequested();
            var x = tileX * 256;
            var y = tileY * 256;
            var tile = textSource.RenderTile(x, y,
                Math.Min(256, width - x), Math.Min(256, height - y), token);
            if (tile is not null) results.Add(tile);
            if (++index % 16 == 0 || index == occupied.Count)
                progress.Report(15 * index / Math.Max(1, occupied.Count));
        }
        return results.ToArray();
    }

    private static void SaveWithVips(string sourcePath, string outputPath, int rotation,
        AnnotationTile[] tiles, TextAnnotationSource? textSource,
        CancellationToken token, IProgress<int> progress, bool multipageTiff = false)
    {
        // A bounded worker/cache budget prevents libvips from retaining large decoded
        // strips while a huge source is being encoded. The source stays lazy.
        NetVips.NetVips.Concurrency = Math.Clamp(Environment.ProcessorCount, 2, 4);
        NetVips.Cache.MaxMem = 128UL * 1024 * 1024;
        NetVips.Cache.MaxFiles = 32;
        using var original = OpenVipsSource(sourcePath, multipageTiff);
        token.ThrowIfCancellationRequested();
        var pageHeight = multipageTiff ? original.PageHeight : original.Height;
        if (pageHeight <= 0 || original.Height % pageHeight != 0)
            throw new StageException("ANNS0002", LanguageManager.Get("AnnotationExportUnsupported")); // ANNS0002
        ValidateTileBounds(tiles, original.Width, pageHeight);
        var overlays = new List<VipsImage>();
        var pages = new List<VipsImage>();
        var textStreams = new List<TextChannelStream>();
        var textChannels = new List<VipsImage>();
        var compositeX = new List<int>();
        var compositeY = new List<int>();
        try
        {
            for (var index = 0; index < tiles.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var tile = tiles[index];
                var overlay = CreateVipsOverlay(tile);
                overlays.Add(overlay);
                compositeX.Add(tile.X);
                compositeY.Add(tile.Y);
                if (index % 32 == 0 || index + 1 == tiles.Length)
                    progress.Report(15 + (index + 1) * 25 / Math.Max(1, tiles.Length));
            }
            token.ThrowIfCancellationRequested();
            if (textSource is { HasVisibleText: true })
            {
                // Separate distant notes into bounded overlays. A single union
                // across opposite corners of a huge image would make two PNM
                // readers synthesize all the transparent pixels between them.
                // Intersecting rectangles are merged so their pixels cannot
                // be composited twice.
                foreach (var textBounds in TextOverlayRegions(textSource.VisibleSourceBounds,
                    original.Width, pageHeight))
                {
                    token.ThrowIfCancellationRequested();
                    VipsImage textOverlay;
                    if ((long)textBounds.Width * textBounds.Height <= 4_194_304)
                    {
                        // Small notes use one capped RGBA buffer and avoid
                        // native stream callbacks before output creation.
                        textOverlay = CreateSmallTextOverlay(textSource, textBounds,
                            token, progress);
                    }
                    else
                    {
                        var sharedTextCache = new SharedTextStripCache(textSource,
                            textBounds.X, textBounds.Y, textBounds.Width, textBounds.Height, token);
                        var rgbStream = new TextChannelStream(sharedTextCache,
                            textBounds.Width, textBounds.Height, alpha: false, token);
                        var alphaStream = new TextChannelStream(sharedTextCache,
                            textBounds.Width, textBounds.Height, alpha: true, token);
                        textStreams.Add(rgbStream);
                        textStreams.Add(alphaStream);
                        progress.Report(25);
                        var rgb = VipsImage.PpmloadStream(rgbStream, memory: false,
                            access: NetVips.Enums.Access.Sequential);
                        textChannels.Add(rgb);
                        progress.Report(30);
                        var alpha = VipsImage.PpmloadStream(alphaStream, memory: false,
                            access: NetVips.Enums.Access.Sequential);
                        textChannels.Add(alpha);
                        progress.Report(35);
                        textOverlay = rgb.Bandjoin(alpha);
                    }
                    overlays.Add(textOverlay);
                    compositeX.Add(textBounds.X);
                    compositeY.Add(textBounds.Y);
                }
            }
            // One composite operation replaces a full-image pipeline node per tile.
            // The text layer comes last, matching the on-screen stacking order.
            // Coordinates are source pixels, independent of display zoom.
            var compositeInputs = overlays.ToArray();
            using var composited = compositeInputs.Length == 0 ? original.Copy() : original.Composite(
                compositeInputs,
                Enumerable.Repeat(NetVips.Enums.BlendMode.Over, compositeInputs.Length).ToArray(),
                compositeX.ToArray(), compositeY.ToArray());
            progress.Report(40);
            // Composite evaluates in float and adds an alpha band. Without restoring
            // the source depth/band count, a large 8-bit RGB TIFF can grow into a
            // 32-bit RGBA TIFF many times larger than the original.
            using var sourceDepth = composited.Format == original.Format
                ? composited.Copy() : composited.Cast(original.Format);
            using var sourceBands = !original.HasAlpha() && sourceDepth.Bands == original.Bands + 1
                ? sourceDepth.ExtractBand(0, n: original.Bands) : sourceDepth.Copy();
            // A rotation of the tall TIFF stack would mix pages. Rotate each
            // page separately and concatenate them lazily in the same order.
            using var oriented = multipageTiff && rotation != 0
                ? RotateTiffPages(sourceBands, pageHeight, rotation, pages, token)
                : rotation switch
                {
                    90 => sourceBands.Rot90(),
                    180 => sourceBands.Rot180(),
                    270 => sourceBands.Rot270(),
                    _ => sourceBands.Copy()
                };
            oriented.SetProgress(new Progress<int>(percent => progress.Report(40 + Math.Clamp(percent, 0, 100) * 55 / 100)), token);
            progress.Report(40);
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
            if (extension is ".tif" or ".tiff")
            {
                if (multipageTiff) options["page_height"] = rotation is 90 or 270 ? original.Width : pageHeight;
                // Classic TIFF uses 32-bit offsets and fails around 4 GiB. A
                // 10 GiB source can easily exceed that after editing, even if
                // the input happened to be compressed more efficiently.
                if (new FileInfo(sourcePath).Length >= 2L * 1024 * 1024 * 1024 ||
                    (long)oriented.Width * oriented.Height * Math.Max(1, oriented.Bands) >= 3L * 1024 * 1024 * 1024)
                    options["bigtiff"] = true;
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
            for (var i = pages.Count - 1; i >= 0; i--) pages[i].Dispose();
            for (var i = overlays.Count - 1; i >= 0; i--) overlays[i].Dispose();
            for (var i = textChannels.Count - 1; i >= 0; i--) textChannels[i].Dispose();
            for (var i = textStreams.Count - 1; i >= 0; i--) textStreams[i].Dispose();
        }
    }

    private static VipsImage OpenVipsSource(string sourcePath, bool multipageTiff)
    {
        if (!multipageTiff)
            return VipsImage.NewFromFile(sourcePath, access: NetVips.Enums.Access.Sequential);
        try
        {
            return VipsImage.Tiffload(sourcePath, n: -1, access: NetVips.Enums.Access.Sequential);
        }
        catch (VipsException error)
        {
            // Variable-size TIFF pages cannot form libvips' lazy page stack.
            // This is unsupported input; encoder and disk failures later in
            // SaveWithVips continue to use ANNS0003 instead.
            throw new StageException("ANNS0002", LanguageManager.Get("AnnotationExportUnsupported"), error); // ANNS0002
        }
    }

    /// <summary>
    /// Groups intersecting source-pixel text rectangles while keeping distant
    /// notes separate. This bounds stream work by annotated area instead of
    /// the potentially enormous empty space between notes.
    /// </summary>
    private static IReadOnlyList<(int X, int Y, int Width, int Height)> TextOverlayRegions(
        IReadOnlyList<System.Windows.Rect> bounds, int sourceWidth, int sourceHeight)
    {
        var regions = bounds.Where(bound => !bound.IsEmpty && bound.Width > 0 && bound.Height > 0)
            .Select(bound => System.Windows.Rect.Intersect(bound,
                new System.Windows.Rect(0, 0, sourceWidth, sourceHeight)))
            .Where(bound => !bound.IsEmpty && bound.Width > 0 && bound.Height > 0)
            .Select(bound => new System.Windows.Rect(Math.Floor(bound.Left),
                Math.Floor(bound.Top), Math.Ceiling(bound.Right) - Math.Floor(bound.Left),
                Math.Ceiling(bound.Bottom) - Math.Floor(bound.Top)))
            .ToList();
        if (regions.Count == 0)
            throw new StageException("ANNS0001", LanguageManager.Get("AnnotationExportInvalid")); // ANNS0001
        // A merge can connect another region that did not touch the original
        // rectangle, so restart scanning after each successful union.
        var merged = true;
        while (merged)
        {
            merged = false;
            for (var i = 0; i < regions.Count && !merged; i++)
                for (var j = i + 1; j < regions.Count; j++)
                    if (regions[i].IntersectsWith(regions[j]))
                    {
                        regions[i] = System.Windows.Rect.Union(regions[i], regions[j]);
                        regions.RemoveAt(j);
                        merged = true;
                        break;
                    }
        }
        return regions.Select(bound =>
        {
            var x0 = (int)Math.Clamp(Math.Floor(bound.Left), 0, sourceWidth);
            var y0 = (int)Math.Clamp(Math.Floor(bound.Top), 0, sourceHeight);
            var x1 = (int)Math.Clamp(Math.Ceiling(bound.Right), 0, sourceWidth);
            var y1 = (int)Math.Clamp(Math.Ceiling(bound.Bottom), 0, sourceHeight);
            return (X: x0, Y: y0, Width: x1 - x0, Height: y1 - y0);
        }).ToArray();
    }

    /// <summary>
    /// Rasterizes a small text bounding box into one capped RGBA overlay. The
    /// background worker requests at most one 256 by 256 tile from the STA text
    /// renderer at a time and reports progress before native encoding starts.
    /// </summary>
    private static VipsImage CreateSmallTextOverlay(TextAnnotationSource source,
        (int X, int Y, int Width, int Height) bounds, CancellationToken token,
        IProgress<int> progress)
    {
        var rowStride = checked(bounds.Width * 4);
        var rgba = new byte[checked(rowStride * bounds.Height)];
        var columns = (bounds.Width + 255L) / 256;
        var rows = (bounds.Height + 255L) / 256;
        var total = columns * rows;
        long finished = 0;
        for (var tileY = 0; tileY < bounds.Height; tileY += 256)
        {
            for (var tileX = 0; tileX < bounds.Width; tileX += 256)
            {
                token.ThrowIfCancellationRequested();
                var tile = source.RenderTile(bounds.X + tileX, bounds.Y + tileY,
                    Math.Min(256, bounds.Width - tileX),
                    Math.Min(256, bounds.Height - tileY), token);
                if (tile is not null)
                    for (var y = 0; y < tile.Height; y++)
                    {
                        var sourceOffset = y * tile.Width * 4;
                        var targetOffset = (tileY + y) * rowStride + tileX * 4;
                        for (var x = 0; x < tile.Width; x++)
                        {
                            var from = sourceOffset + x * 4;
                            var to = targetOffset + x * 4;
                            rgba[to] = tile.BgraPixels[from + 2];
                            rgba[to + 1] = tile.BgraPixels[from + 1];
                            rgba[to + 2] = tile.BgraPixels[from];
                            rgba[to + 3] = tile.BgraPixels[from + 3];
                        }
                    }
                finished++;
                progress.Report((int)(15 + finished * 25 / total));
            }
        }
        using var raw = VipsImage.NewFromMemoryCopy<byte>(rgba,
            bounds.Width, bounds.Height, 4, NetVips.Enums.BandFormat.Uchar);
        return raw.Copy(interpretation: NetVips.Enums.Interpretation.Srgb);
    }

    private static VipsImage RotateTiffPages(VipsImage stack, int pageHeight, int rotation,
        List<VipsImage> ownedPages, CancellationToken token)
    {
        var count = stack.Height / pageHeight;
        for (var index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            using var crop = stack.Crop(0, index * pageHeight, stack.Width, pageHeight);
            ownedPages.Add(rotation switch
            {
                90 => crop.Rot90(),
                180 => crop.Rot180(),
                270 => crop.Rot270(),
                _ => crop.Copy()
            });
        }
        return VipsImage.Arrayjoin(ownedPages.ToArray(), across: 1, shim: 0);
    }

    /// <summary>
    /// Shares bounded tile output between the RGB and alpha PNM readers. The
    /// current source strip is at most 128 MiB and is discarded on crossing a
    /// strip boundary; each text tile is normally rasterized once, not twice.
    /// </summary>
    private sealed class SharedTextStripCache
    {
        private readonly TextAnnotationSource source;
        private readonly int width;
        private readonly int height;
        private readonly int originX;
        private readonly int originY;
        private readonly CancellationToken token;
        private readonly int stripHeight;
        private readonly Dictionary<(int X, int Y), AnnotationTile?> tiles = [];
        private readonly object gate = new();
        private int cachedStripY = -1;
        private readonly System.Windows.Rect[] textBounds;

        internal SharedTextStripCache(TextAnnotationSource source, int originX, int originY,
            int width, int height, CancellationToken token)
        {
            this.source = source;
            this.originX = originX;
            this.originY = originY;
            this.width = width;
            this.height = height;
            this.token = token;
            stripHeight = (int)Math.Clamp(128L * 1024 * 1024 / ((long)width * 4), 1, 256);
            textBounds = source.SourceBounds.ToArray();
        }

        internal int StripHeight => stripHeight;

        internal AnnotationTile? Get(int x, int y)
        {
            var stripY = y / stripHeight * stripHeight;
            var tileX = x / 256 * 256;
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                if (cachedStripY != stripY)
                {
                    // The native PNM reader calls Get for many small pieces of
                    // the same row. Clearing by scanning dictionary keys on
                    // every call made wide images quadratic in tile count.
                    // A strip change is the only time cached tiles can expire.
                    tiles.Clear();
                    cachedStripY = stripY;
                }
                var key = (tileX, stripY);
                if (!tiles.TryGetValue(key, out var tile))
                {
                    var tileWidth = Math.Min(256, width - tileX);
                    var tileHeight = Math.Min(stripHeight, height - stripY);
                    var region = new System.Windows.Rect(originX + tileX,
                        originY + stripY, tileWidth, tileHeight);
                    // A union bounding box can contain a huge empty gap
                    // between separate text rectangles. Skip those regions
                    // without asking the STA raster worker to shape them.
                    tile = textBounds.Any(bound => bound.IntersectsWith(region))
                        ? source.RenderTile(originX + tileX, originY + stripY,
                            tileWidth, tileHeight, token) : null;
                    tiles[key] = tile;
                }
                return tile;
            }
        }
    }

    /// <summary>
    /// A seekable virtual P6/P5 image. libvips reads RGB and alpha as separate
    /// sequential sources, then joins them into one transparent overlay. No
    /// image-sized byte array or temporary raw file is allocated.
    /// </summary>
    private sealed class TextChannelStream : Stream
    {
        private readonly SharedTextStripCache source;
        private readonly int width;
        private readonly int height;
        private readonly bool alpha;
        private readonly CancellationToken token;
        private readonly byte[] header;
        private readonly long length;
        private readonly int channels;
        private long position;

        internal TextChannelStream(SharedTextStripCache source, int width, int height,
            bool alpha, CancellationToken token)
        {
            this.source = source;
            this.width = width;
            this.height = height;
            this.alpha = alpha;
            this.token = token;
            channels = alpha ? 1 : 3;
            header = Encoding.ASCII.GetBytes($"{(alpha ? "P5" : "P6")}\n{width} {height}\n255\n");
            length = checked(header.LongLength + (long)width * height * channels);
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => position;
            set => position = value is >= 0 && value <= length ? value
                : throw new ArgumentOutOfRangeException(nameof(value));
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> destination)
        {
            token.ThrowIfCancellationRequested();
            if (position >= length || destination.IsEmpty) return 0;
            var copied = 0;
            if (position < header.Length)
            {
                var headerCount = Math.Min(destination.Length, header.Length - (int)position);
                header.AsSpan((int)position, headerCount).CopyTo(destination);
                position += headerCount;
                copied += headerCount;
                destination = destination[headerCount..];
            }
            while (!destination.IsEmpty && position < length)
            {
                token.ThrowIfCancellationRequested();
                var pixelBytes = position - header.Length;
                var pixelIndex = pixelBytes / channels;
                var band = (int)(pixelBytes % channels);
                var y = (int)(pixelIndex / width);
                var x = (int)(pixelIndex % width);
                if (y >= height) break;
                var tileX = x / 256 * 256;
                var stripY = y / source.StripHeight * source.StripHeight;
                var cachedTile = source.Get(x, y);
                var tileWidth = Math.Min(256, width - tileX);
                var localX = x - tileX;
                var localY = y - stripY;
                var pixelsAvailable = Math.Min(tileWidth - localX,
                    (destination.Length + band + channels - 1) / channels);
                var bytesAvailable = Math.Min(destination.Length,
                    pixelsAvailable * channels - band);
                var output = destination[..bytesAvailable];
                if (cachedTile is null) output.Clear();
                else
                {
                    var sourcePixels = cachedTile.BgraPixels;
                    for (var i = 0; i < bytesAvailable; i++)
                    {
                        var component = (band + i) % channels;
                        var pixelOffset = ((localY * tileWidth + localX) +
                            (band + i) / channels) * 4;
                        output[i] = alpha ? sourcePixels[pixelOffset + 3]
                            : sourcePixels[pixelOffset + (2 - component)];
                    }
                }
                destination = destination[bytesAvailable..];
                position += bytesAvailable;
                copied += bytesAvailable;
            }
            return copied;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var basis = origin switch
            {
                SeekOrigin.Begin => 0L,
                SeekOrigin.Current => position,
                SeekOrigin.End => length,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            Position = checked(basis + offset);
            return position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
            if (IsMultipage(sourcePath))
            {
                var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
                if (extension is ".tif" or ".tiff")
                {
                    using var sourceHeader = VipsImage.NewFromFile(sourcePath, access: NetVips.Enums.Access.Sequential);
                    using var outputHeader = VipsImage.NewFromFile(outputPath, access: NetVips.Enums.Access.Sequential);
                    var sourcePages = sourceHeader.Contains("n-pages")
                        ? Convert.ToInt32(sourceHeader.Get("n-pages"), CultureInfo.InvariantCulture) : 1;
                    var outputPages = outputHeader.Contains("n-pages")
                        ? Convert.ToInt32(outputHeader.Get("n-pages"), CultureInfo.InvariantCulture) : 1;
                    if (sourcePages != outputPages)
                        throw new StageException("ANNS0004", LanguageManager.Get("AnnotationExportVerificationFailed")); // ANNS0004
                }
                else if (MagickImageInfo.ReadCollection(sourcePath).Count() !=
                    MagickImageInfo.ReadCollection(outputPath).Count())
                    throw new StageException("ANNS0004", LanguageManager.Get("AnnotationExportVerificationFailed")); // ANNS0004
            }
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
