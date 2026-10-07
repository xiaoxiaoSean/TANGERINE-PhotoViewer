using ImageMagick;
using NetVips;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
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
            // The Photoshop merged-image reader exports pixels, not a layered
            // Photoshop document. Offer PNG instead of a misleading PSD/PSB saver.
            if (IsPhotoshopContent(sourcePath)) return false;
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

            // The reader that opens the source must also supply the pixels to
            // the exporter. In particular, libvips cannot reopen many PSB files
            // that the bounded Photoshop reader displays successfully.
            PsdCompositeReader.Info? photoshop = null;
            if (IsPhotoshopContent(source))
            {
                try { photoshop = PsdCompositeReader.Inspect(source, token); }
                catch (NotSupportedException)
                {
                    // The generic decoder may understand modes outside the
                    // bounded composite reader's supported subset.
                }
            }
            var multipage = photoshop is null && IsMultipage(source);
            progress.Report(5);
            if (photoshop is { } psdInfo)
            {
                var rawPath = Path.Combine(Path.GetTempPath(), "TangerinePhotoViewer",
                    "AnnotationCache", Guid.NewGuid().ToString("N") + ".rgba");
                Directory.CreateDirectory(Path.GetDirectoryName(rawPath)!);
                try
                {
                    PsdCompositeReader.WriteInterleavedRgba(source, psdInfo, rawPath,
                        token, new SynchronousProgress(value =>
                            progress.Report(5 + Math.Clamp(value, 0, 100) * 9 / 100)));
                    token.ThrowIfCancellationRequested();
                    using var raw = VipsImage.Rawload(rawPath, psdInfo.Width,
                        psdInfo.Height, 4, format: NetVips.Enums.BandFormat.Uchar,
                        interpretation: NetVips.Enums.Interpretation.Srgb,
                        memory: false, access: NetVips.Enums.Access.Sequential);
                    SaveWithVips(source, temporaryPath, rotation, tiles, textSource,
                        token, progress, suppliedSource: raw);
                }
                finally { TryDelete(rawPath); }
            }
            else if (multipage)
            {
                if (IsTiffContent(source))
                {
                    try
                    {
                        SaveWithVips(source, temporaryPath, rotation, tiles, textSource,
                            token, progress, multipageTiff: true);
                    }
                    catch (Exception error) when (error is VipsException ||
                        error is StageException { StageCode: "ANNS0002" })
                    {
                        TryDelete(temporaryPath);
                        var fallbackTiles = textSource is { HasVisibleText: true }
                            ? CombineSmallTextTiles(tiles, textSource,
                                ReadDimensions(source), token, progress) : tiles;
                        SaveMultipage(source, temporaryPath, rotation, fallbackTiles,
                            token, progress);
                    }
                }
                else
                {
                    var multipageTiles = textSource is null ? tiles :
                        CombineSmallTextTiles(tiles, textSource,
                            ReadDimensions(source), token, progress);
                    SaveMultipage(source, temporaryPath, rotation, multipageTiles, token, progress);
                }
            }
            else
            {
                try
                {
                    SaveWithVips(source, temporaryPath, rotation, tiles, textSource, token, progress);
                }
                catch (Exception error) when (error is VipsException ||
                    error is StageException { StageCode: "ANNS0002" })
                {
                    // A libvips loader or saver can be absent even when Magick
                    // supports the file. Its disk-backed pixel cache keeps the
                    // general fallback within the configured memory budget.
                    TryDelete(temporaryPath);
                    var fallbackTiles = textSource is { HasVisibleText: true }
                        ? CombineSmallTextTiles(tiles, textSource,
                            ReadDimensions(source), token, progress)
                        : tiles;
                    SaveWithMagick(source, temporaryPath, rotation, fallbackTiles, token, progress);
                }
            }

            token.ThrowIfCancellationRequested();
            VerifyDimensions(source, temporaryPath, rotation, photoshop, multipage);
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

    private static bool IsMultipage(string source)
    {
        // Probe the decoder's page metadata, not the filename. A renamed TIFF
        // or GIF must take the same export path as one with its usual extension.
        try
        {
            using var image = VipsImage.NewFromFile(source, access: NetVips.Enums.Access.Sequential);
            return image.Contains("n-pages") &&
                Convert.ToInt32(image.Get("n-pages"), CultureInfo.InvariantCulture) > 1;
        }
        catch (VipsException) { return IsMultipageFallback(source); }
    }

    private static bool IsMultipageFallback(string source)
    {
        // Header enumeration is bounded to two entries. It does not decode a
        // frame collection, even for a very large source file.
        using var stream = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var format = ImageLoader.DetectFallbackFormat(stream);
        LimitMagickExportCache();
        var frames = format == MagickFormat.Unknown
            ? MagickImageInfo.ReadCollection(stream)
            : MagickImageInfo.ReadCollection(stream,
                new MagickReadSettings { Format = format });
        return frames.Take(2).Count() > 1;
    }

    private static bool IsTiffContent(string source)
    {
        using var stream = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        Span<byte> signature = stackalloc byte[4];
        if (stream.Read(signature) != signature.Length) return false;
        return signature.SequenceEqual(new byte[] { 0x49, 0x49, 0x2a, 0x00 }) ||
            signature.SequenceEqual(new byte[] { 0x4d, 0x4d, 0x00, 0x2a }) ||
            signature.SequenceEqual(new byte[] { 0x49, 0x49, 0x2b, 0x00 }) ||
            signature.SequenceEqual(new byte[] { 0x4d, 0x4d, 0x00, 0x2b });
    }

    private static bool IsPhotoshopContent(string source)
    {
        using var stream = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        Span<byte> header = stackalloc byte[6];
        return stream.Read(header) == header.Length &&
            header[..4].SequenceEqual("8BPS"u8) &&
            BinaryPrimitives.ReadUInt16BigEndian(header[4..]) is 1 or 2;
    }

    private static bool IsGifContent(string source)
    {
        using var stream = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        Span<byte> signature = stackalloc byte[6];
        return stream.Read(signature) == signature.Length &&
            (signature.SequenceEqual("GIF87a"u8) || signature.SequenceEqual("GIF89a"u8));
    }

    private sealed class SynchronousProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    private static AnnotationTile[] CombineSmallTextTiles(AnnotationTile[] brushTiles,
        TextAnnotationSource textSource, (int Width, int Height) dimensions, CancellationToken token,
        IProgress<int> progress)
    {
        // Text is sampled into source-pixel tiles with a separate memory cap.
        const long textLimit = 256L * 1024 * 1024;
        var width = dimensions.Width;
        var height = dimensions.Height;
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
        CancellationToken token, IProgress<int> progress, bool multipageTiff = false,
        VipsImage? suppliedSource = null)
    {
        // A bounded worker/cache budget prevents libvips from retaining large decoded
        // strips while a huge source is being encoded. The source stays lazy.
        NetVips.NetVips.Concurrency = Math.Clamp(Environment.ProcessorCount, 2, 4);
        NetVips.Cache.MaxMem = 128UL * 1024 * 1024;
        NetVips.Cache.MaxFiles = 32;
        using var original = suppliedSource is null
            ? OpenVipsSource(sourcePath, multipageTiff) : suppliedSource.Copy();
        token.ThrowIfCancellationRequested();
        var pageHeight = multipageTiff ? original.PageHeight : original.Height;
        if (pageHeight <= 0 || original.Height % pageHeight != 0)
            throw new StageException("ANNS0002", LanguageManager.Get("AnnotationExportUnsupported")); // ANNS0002
        ValidateTileBounds(tiles, original.Width, pageHeight);
        var overlays = new List<VipsImage>();
        var pages = new List<VipsImage>();
        var textRawFiles = new List<string>();
        var compositeX = new List<int>();
        var compositeY = new List<int>();
        string? sourceProfilePath = null;
        try
        {
            // Convert annotation colours to the source device space, leaving
            // source pixels themselves untouched. A CMYK TIFF cannot be
            // composited through the default sRGB route, and copying an sRGB
            // result back to four CMYK bands would corrupt its colour values.
            if (original.Interpretation == NetVips.Enums.Interpretation.Cmyk &&
                original.Contains("icc-profile-data") &&
                original.Get("icc-profile-data") is byte[] profile && profile.Length > 0)
            {
                sourceProfilePath = Path.Combine(Path.GetDirectoryName(outputPath)!,
                    "." + Guid.NewGuid().ToString("N") + ".icc");
                File.WriteAllBytes(sourceProfilePath, profile);
            }
            for (var index = 0; index < tiles.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var tile = tiles[index];
                var overlay = PrepareOverlayForSource(CreateVipsOverlay(tile),
                    original, sourceProfilePath);
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
                        // The published runtime could not resolve ppmload_source.
                        // Write only occupied RGBA rows into a sparse temporary
                        // file, then let rawload read it lazily during encode.
                        // Empty areas remain filesystem holes, so a huge text
                        // rectangle does not reserve an image-sized RAM buffer.
                        var rawPath = Path.Combine(Path.GetTempPath(),
                            "TangerinePhotoViewer", "AnnotationCache",
                            Guid.NewGuid().ToString("N") + ".rgba");
                        Directory.CreateDirectory(Path.GetDirectoryName(rawPath)!);
                        textRawFiles.Add(rawPath);
                        WriteSparseTextOverlay(textSource, textBounds, rawPath, token, progress);
                        textOverlay = VipsImage.Rawload(rawPath,
                            textBounds.Width, textBounds.Height, 4,
                            format: NetVips.Enums.BandFormat.Uchar,
                            interpretation: NetVips.Enums.Interpretation.Srgb,
                            memory: false,
                            access: NetVips.Enums.Access.Sequential);
                    }
                    overlays.Add(PrepareOverlayForSource(textOverlay,
                        original, sourceProfilePath));
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
                compositeX.ToArray(), compositeY.ToArray(),
                compositingSpace: original.Interpretation);
            progress.Report(40);
            // Composite evaluates in float and adds an alpha band. Without restoring
            // the source depth/band count, a large 8-bit RGB TIFF can grow into a
            // 32-bit RGBA TIFF many times larger than the original.
            using var sourceDepth = composited.Format == original.Format
                ? composited.Copy() : composited.Cast(original.Format);
            using var sourceBands = sourceDepth.Bands == original.Bands + 1 && !original.HasAlpha()
                ? sourceDepth.ExtractBand(0, n: original.Bands) : sourceDepth.Copy();
            if (sourceBands.Bands != original.Bands)
                throw new StageException("ANNS0002", LanguageManager.Get("AnnotationExportUnsupported")); // ANNS0002
            using var restoredSpace = sourceBands.Interpretation == original.Interpretation
                ? sourceBands.Copy() : sourceBands.Copy(interpretation: original.Interpretation);
            // A rotation of the tall TIFF stack would mix pages. Rotate each
            // page separately and concatenate them lazily in the same order.
            using var oriented = multipageTiff && rotation != 0
                ? RotateTiffPages(restoredSpace, pageHeight, rotation, pages, token)
                : rotation switch
                {
                    90 => restoredSpace.Rot90(),
                    180 => restoredSpace.Rot180(),
                    270 => restoredSpace.Rot270(),
                    _ => restoredSpace.Copy()
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
            foreach (var rawPath in textRawFiles) TryDelete(rawPath);
            if (sourceProfilePath is not null) TryDelete(sourceProfilePath);
        }
    }

    /// <summary>
    /// Takes ownership of an sRGB RGBA overlay and returns an image with the
    /// source's device channels plus its original alpha. Transform only the
    /// tiny annotation layer: a multi-gigabyte original must stay lazy and
    /// must retain all untouched pixel values and its embedded profile.
    /// </summary>
    private static VipsImage PrepareOverlayForSource(VipsImage rgba,
        VipsImage original, string? sourceProfilePath)
    {
        if ((original.Interpretation is NetVips.Enums.Interpretation.Srgb or
            NetVips.Enums.Interpretation.Rgb) && sourceProfilePath is null &&
            original.Format == NetVips.Enums.BandFormat.Uchar)
            return rgba;
        if (original.Interpretation is not (NetVips.Enums.Interpretation.Srgb or
            NetVips.Enums.Interpretation.Rgb or NetVips.Enums.Interpretation.Rgb16 or
            NetVips.Enums.Interpretation.Bw or NetVips.Enums.Interpretation.Grey16 or
            NetVips.Enums.Interpretation.Cmyk))
        {
            rgba.Dispose();
            throw new StageException("ANNS0002", LanguageManager.Get("AnnotationExportUnsupported")); // ANNS0002
        }
        using (rgba)
        using (var rgb = rgba.ExtractBand(0, n: 3))
        using (var alpha = rgba.ExtractBand(3))
        {
            var depth = original.Format == NetVips.Enums.BandFormat.Ushort ? 16 : 8;
            using var converted = sourceProfilePath is not null
                ? rgb.IccTransform(sourceProfilePath, inputProfile: "srgb", depth: depth)
                : original.Interpretation == NetVips.Enums.Interpretation.Cmyk
                    ? rgb.IccTransform("cmyk", inputProfile: "srgb", depth: depth)
                    : rgb.Colourspace(original.Interpretation);
            using var deviceColour = converted.Format == original.Format
                ? converted.Copy() : converted.Cast(original.Format);
            if (deviceColour.Bands != original.Bands - (original.HasAlpha() ? 1 : 0))
                throw new StageException("ANNS0002", LanguageManager.Get("AnnotationExportUnsupported")); // ANNS0002
            // Once ICC conversion produces 16-bit colour, its alpha must be
            // 16-bit too: leaving 255 as the maximum would make opaque ink
            // almost transparent against a 65535-range TIFF source.
            using var deviceAlpha = depth == 16
                ? (alpha * 257).Cast(NetVips.Enums.BandFormat.Ushort)
                : alpha.Copy();
            using var sourceAlpha = deviceAlpha.Format == original.Format
                ? deviceAlpha.Copy() : deviceAlpha.Cast(original.Format);
            using var deviceRgba = deviceColour.Bandjoin(sourceAlpha);
            return deviceRgba.Copy(interpretation: original.Interpretation);
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

    /// <summary>
    /// Writes the original-pixel text layer as a sparse RGBA file. Only rows
    /// containing rendered glyphs are written; a later rawload reads this
    /// file lazily without requiring the optional PPM source operation in the
    /// native libvips package. The file remains open until every tile has been
    /// written, then its length is checked before it enters the save graph.
    /// </summary>
    private static void WriteSparseTextOverlay(TextAnnotationSource source,
        (int X, int Y, int Width, int Height) bounds, string path,
        CancellationToken token, IProgress<int> progress)
    {
        var rowStride = checked((long)bounds.Width * 4);
        var byteLength = checked(rowStride * bounds.Height);
        var columns = (bounds.Width + 255L) / 256;
        var rows = (bounds.Height + 255L) / 256;
        var total = checked(columns * rows);
        long finished = 0;
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
        // Windows does not promise that a plain SetLength leaves unallocated
        // ranges sparse. Mark the file before extending it, or a large text
        // rectangle may consume its entire raw size on the system drive.
        if (!SetSparse(stream.SafeFileHandle))
            throw new StageException("ANNS0002", LanguageManager.Get("AnnotationExportUnsupported")); // ANNS0002
        stream.SetLength(byteLength);
        for (var tileY = 0; tileY < bounds.Height; tileY += 256)
            for (var tileX = 0; tileX < bounds.Width; tileX += 256)
            {
                token.ThrowIfCancellationRequested();
                var tile = source.RenderTile(bounds.X + tileX, bounds.Y + tileY,
                    Math.Min(256, bounds.Width - tileX),
                    Math.Min(256, bounds.Height - tileY), token);
                if (tile is not null)
                {
                    var rgba = new byte[checked(tile.Width * 4)];
                    for (var y = 0; y < tile.Height; y++)
                    {
                        token.ThrowIfCancellationRequested();
                        var sourceRow = y * tile.Width * 4;
                        for (var x = 0; x < tile.Width; x++)
                        {
                            var from = sourceRow + x * 4;
                            var to = x * 4;
                            rgba[to] = tile.BgraPixels[from + 2];
                            rgba[to + 1] = tile.BgraPixels[from + 1];
                            rgba[to + 2] = tile.BgraPixels[from];
                            rgba[to + 3] = tile.BgraPixels[from + 3];
                        }
                        var offset = checked(((long)tileY + y) * rowStride + (long)tileX * 4);
                        RandomAccess.Write(stream.SafeFileHandle, rgba, offset);
                    }
                }
                finished++;
                if (finished % 16 == 0 || finished == total)
                    progress.Report((int)(15 + finished * 20 / total));
            }
        stream.Flush(flushToDisk: true);
        if (stream.Length != byteLength)
            throw new StageException("ANNS0003", LanguageManager.Get("AnnotationExportFailed")); // ANNS0003
    }

    private const uint FsctlSetSparse = 0x000900C4;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint controlCode,
        IntPtr input, uint inputLength, IntPtr output, uint outputLength,
        out uint bytesReturned, IntPtr overlapped);

    private static bool SetSparse(SafeFileHandle file) =>
        DeviceIoControl(file, FsctlSetSparse, IntPtr.Zero, 0,
            IntPtr.Zero, 0, out _, IntPtr.Zero);

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
        LimitMagickExportCache();
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var format = ImageLoader.DetectFallbackFormat(input);
        using var frames = format == MagickFormat.Unknown
            ? new MagickImageCollection(input)
            : new MagickImageCollection(input,
                new MagickReadSettings { Format = format });
        if (frames.Count == 0)
            throw new StageException("ANNS0001", LanguageManager.Get("AnnotationExportInvalid")); // ANNS0001
        // GIF frames can be sparse rectangles relative to their page canvas.
        // Coalescing reconstructs those pixels before annotating the displayed
        // first frame, and preserves the frame count, delays, and loop metadata.
        if (IsGifContent(sourcePath))
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
        LimitMagickExportCache();
        using var image = OpenMagickSource(sourcePath);
        token.ThrowIfCancellationRequested();
        CompositeTiles(image, tiles, token, progress);
        if (rotation != 0) image.Rotate(rotation);
        token.ThrowIfCancellationRequested();
        image.Write(outputPath);
        progress.Report(95);
    }

    private static MagickImage OpenMagickSource(string sourcePath)
    {
        // A few formats need an explicit content-derived hint when opened from
        // a stream or from a filename with an unrelated extension.
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var format = ImageLoader.DetectFallbackFormat(source);
        return format == MagickFormat.Unknown
            ? new MagickImage(source)
            : new MagickImage(source, new MagickReadSettings { Format = format });
    }

    private static void LimitMagickExportCache()
    {
        // ImageMagick may spill decoded pixels to disk for formats without a
        // libvips loader. Keep managed and native memory bounded per process.
        const ulong memoryBudget = 256UL * 1024 * 1024;
        ResourceLimits.Memory = Math.Min(ResourceLimits.Memory, memoryBudget);
        ResourceLimits.Area = Math.Min(ResourceLimits.Area, memoryBudget / 4);
        ResourceLimits.Disk = Math.Min(ResourceLimits.Disk, 64UL * 1024 * 1024 * 1024);
        ResourceLimits.Thread = Math.Min(ResourceLimits.Thread,
            (uint)Math.Clamp(Environment.ProcessorCount, 2, 4));
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

    private static void VerifyDimensions(string sourcePath, string outputPath, int rotation,
        PsdCompositeReader.Info? photoshop, bool multipage)
    {
        try
        {
            var (outputWidth, outputHeight) = ReadDimensions(outputPath);
            var (sourceWidth, sourceHeight) = photoshop is { } info
                ? (info.Width, info.Height) : ReadDimensions(sourcePath);
            var expectedWidth = rotation is 90 or 270 ? sourceHeight : sourceWidth;
            var expectedHeight = rotation is 90 or 270 ? sourceWidth : sourceHeight;
            if (outputWidth != expectedWidth || outputHeight != expectedHeight)
                throw new StageException("ANNS0004", LanguageManager.Get("AnnotationExportVerificationFailed")); // ANNS0004
            if (multipage)
            {
                if (IsTiffContent(sourcePath))
                {
                    using var sourceHeader = VipsImage.Tiffload(sourcePath, n: -1,
                        access: NetVips.Enums.Access.Sequential);
                    using var outputHeader = VipsImage.NewFromFile(outputPath, access: NetVips.Enums.Access.Sequential);
                    var sourcePages = sourceHeader.PageHeight > 0
                        ? sourceHeader.Height / sourceHeader.PageHeight : 1;
                    var outputPages = outputHeader.Contains("n-pages")
                        ? Convert.ToInt32(outputHeader.Get("n-pages"), CultureInfo.InvariantCulture) : 1;
                    if (sourcePages != outputPages)
                        throw new StageException("ANNS0004", LanguageManager.Get("AnnotationExportVerificationFailed")); // ANNS0004
                }
                else
                {
                    using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var output = new FileStream(outputPath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    var sourceFormat = ImageLoader.DetectFallbackFormat(input);
                    var sourceFrames = sourceFormat == MagickFormat.Unknown
                        ? MagickImageInfo.ReadCollection(input)
                        : MagickImageInfo.ReadCollection(input,
                            new MagickReadSettings { Format = sourceFormat });
                    if (sourceFrames.Count() != MagickImageInfo.ReadCollection(output).Count())
                        throw new StageException("ANNS0004", LanguageManager.Get("AnnotationExportVerificationFailed")); // ANNS0004
                }
            }
        }
        catch (StageException) { throw; }
        catch (Exception error)
        {
            throw new StageException("ANNS0004", LanguageManager.Get("AnnotationExportVerificationFailed"), error); // ANNS0004
        }
    }

    private static (int Width, int Height) ReadDimensions(string path)
    {
        try
        {
            using var image = VipsImage.NewFromFile(path,
                access: NetVips.Enums.Access.Sequential);
            if (image.Width > 0 && image.Height > 0)
                return (image.Width, image.Height);
        }
        catch (VipsException) { /* A second installed codec can read the header. */ }
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var format = ImageLoader.DetectFallbackFormat(source);
        var metadata = format == MagickFormat.Unknown
            ? new MagickImageInfo(source)
            : new MagickImageInfo(source, new MagickReadSettings { Format = format });
        return (checked((int)metadata.Width), checked((int)metadata.Height));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
