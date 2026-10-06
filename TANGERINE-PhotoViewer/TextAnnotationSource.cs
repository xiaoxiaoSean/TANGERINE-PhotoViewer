using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TANGERINE_PhotoViewer.DefaultApps;

namespace TANGERINE_PhotoViewer;

/// <summary>
/// Renders immutable text snapshots on demand on one background STA thread. The
/// export worker requests at most a 256 by 256 source-pixel region at a time,
/// so a text rectangle spanning a very large photograph never materializes as
/// one enormous bitmap or as a list of all occupied raster tiles.
/// </summary>
internal sealed class TextAnnotationSource : IDisposable
{
    private sealed record Work(int X, int Y, int Width, int Height,
        CancellationToken Token, TaskCompletionSource<AnnotationTile?> Completion);

    private sealed record Layout(MainWindow.TextNoteSnapshot Note, string AllText,
        FormattedText Text, IReadOnlyList<(double Distance, FormattedText Text)> Shadows,
        Rect SourceBounds, double LayoutScale, Rect InkBounds);

    private readonly BlockingCollection<Work> work = new();
    private readonly Thread worker;
    private readonly MainWindow.TextNoteSnapshot[] notes;
    private readonly Rect[] sourceBounds;
    private Rect[]? inkBounds;
    private readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object lifecycleGate = new();
    private readonly Action<int>? reportProgress;
    private long renderedPixels;
    private readonly long totalPixels;
    private bool disposed;

    internal IReadOnlyList<Rect> SourceBounds => sourceBounds;
    internal IReadOnlyList<Rect> VisibleSourceBounds => notes
        .Select((note, index) => (note, index))
        .Where(item => item.note.Runs.Any(run => !string.IsNullOrWhiteSpace(run.Text)))
        .Select(item => sourceBounds[item.index]).ToArray();
    internal bool HasVisibleText => notes.Any(note =>
        note.Runs.Any(run => !string.IsNullOrWhiteSpace(run.Text)));

    internal TextAnnotationSource(MainWindow.TextNoteSnapshot[] snapshots,
        Action<int>? reportProgress = null)
    {
        notes = snapshots.ToArray();
        sourceBounds = notes.Select(SourceBoundsFor).ToArray();
        this.reportProgress = reportProgress;
        // Count source-space pixels in each text bounding box. Overlap may make
        // the estimate conservative, but a wide note cannot hit 100% after only
        // its first 256-pixel-wide strip.
        totalPixels = Math.Max(1, sourceBounds.Aggregate(0L, (sum, bounds) =>
            checked(sum + checked((long)Math.Ceiling(bounds.Width) *
                (long)Math.Ceiling(bounds.Height)))));
        worker = new Thread(Serve)
        {
            IsBackground = true,
            Name = "On-demand text annotation renderer"
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    /// <summary>
    /// Called only from a background export thread. Multiple callers may queue
    /// requests; the STA worker serializes WPF layout and rasterization safely.
    /// </summary>
    internal AnnotationTile? RenderRegion(int sourceX, int sourceY, int width, int height,
        CancellationToken token)
    {
        if (width is < 1 or > 256 || height is < 1 or > 256 ||
            sourceX < 0 || sourceY < 0)
            throw new StageException("TEXT0004", LanguageManager.Get("TextRenderingFailed")); // TEXT0004
        token.ThrowIfCancellationRequested();
        var region = new Rect(sourceX, sourceY, width, height);
        try { ready.Task.WaitAsync(token).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StageException("TEXT0004", LanguageManager.Get("TextRenderingFailed"), ex); // TEXT0004
        }
        var likelyBounds = inkBounds ?? sourceBounds;
        if (!likelyBounds.Any(bounds => bounds.IntersectsWith(region))) return null;
        var completion = new TaskCompletionSource<AnnotationTile?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            lock (lifecycleGate)
            {
                if (disposed) throw new InvalidOperationException("Text renderer was disposed.");
                work.Add(new Work(sourceX, sourceY, width, height, token, completion), token);
            }
        }
        catch (InvalidOperationException ex)
        {
            throw new StageException("TEXT0004", LanguageManager.Get("TextRenderingFailed"), ex); // TEXT0004
        }
        return completion.Task.WaitAsync(token).GetAwaiter().GetResult();
    }

    internal AnnotationTile? RenderTile(int sourceX, int sourceY, int width, int height,
        CancellationToken token) => RenderRegion(sourceX, sourceY, width, height, token);

    private void Serve()
    {
        Layout[]? layouts = null;
        try
        {
            layouts = notes.Select((note, index) => BuildLayout(note, sourceBounds[index])).ToArray();
            inkBounds = layouts.Select(layout => layout.InkBounds).ToArray();
            ready.TrySetResult(true);
            foreach (var request in work.GetConsumingEnumerable())
            {
                try
                {
                    request.Token.ThrowIfCancellationRequested();
                    request.Completion.TrySetResult(Render(request, layouts));
                    if (reportProgress is not null)
                    {
                        var pixels = Interlocked.Add(ref renderedPixels,
                            (long)request.Width * request.Height);
                        reportProgress((int)Math.Clamp(pixels * 100d / totalPixels, 0, 99));
                    }
                }
                catch (OperationCanceledException)
                {
                    request.Completion.TrySetCanceled(request.Token);
                }
                catch (Exception ex)
                {
                    request.Completion.TrySetException(new StageException("TEXT0004",
                        LanguageManager.Get("TextRenderingFailed"), ex)); // TEXT0004
                }
            }
        }
        catch (Exception ex)
        {
            ready.TrySetException(new StageException("TEXT0004",
                LanguageManager.Get("TextRenderingFailed"), ex)); // TEXT0004
            foreach (var request in work.GetConsumingEnumerable())
                request.Completion.TrySetException(new StageException("TEXT0004",
                    LanguageManager.Get("TextRenderingFailed"), ex)); // TEXT0004
        }
    }

    private static Layout BuildLayout(MainWindow.TextNoteSnapshot note, Rect sourceBounds)
    {
        var allText = string.Concat(note.Runs.Select(run => run.Text));
        var scale = Math.Min(1, Math.Min(2048 / Math.Max(1, note.ReferenceBounds.Width),
            2048 / Math.Max(1, note.ReferenceBounds.Height)));
        var width = Math.Max(1, note.ReferenceBounds.Width * scale);
        var height = Math.Max(1, note.ReferenceBounds.Height * scale);
        // The UI editor accepts large rich-text content, but glyphs after the
        // rectangle's visible lines cannot appear in its exported pixels. Bound
        // shaping by visible line count and a generous per-line glyph budget.
        var smallestRunFont = note.Runs.Select(run => run.FontSizePixels)
            .Where(value => value > 0 && double.IsFinite(value))
            .DefaultIfEmpty(note.ReferenceBounds.Height * note.Settings.HeightFraction)
            .Min();
        var nominalGlyphHeight = Math.Max(1, smallestRunFont * scale);
        var lineBudget = Math.Min(200_000, Math.Max(1,
            (int)Math.Ceiling(height / nominalGlyphHeight) + 1));
        var glyphsPerLine = Math.Min(200_000,
            Math.Max(1, width / Math.Max(1, nominalGlyphHeight * .2)));
        var glyphBudget = (int)Math.Min(200_000,
            Math.Max(256d, lineBudget * glyphsPerLine * 2));
        if (allText.Length > glyphBudget) allText = allText[..glyphBudget];
        var fontSize = Math.Max(.001, note.ReferenceBounds.Height * note.Settings.HeightFraction * scale);
        var text = CreateFormattedText(note, allText.Length == 0 ? " " : allText,
            fontSize, width, height, scale, null);
        (double Distance, FormattedText Text)[] shadows = string.IsNullOrWhiteSpace(allText)
            ? []
            : note.Runs.Where(run => run.HasShadow && run.ShadowSize > 0)
                .Select(run => run.ShadowSize).Distinct().Select(distance =>
                    (Distance: distance, Text: CreateFormattedText(note, allText, fontSize,
                        width, height, scale, distance))).ToArray();
        // Rich text can contain large selected glyphs or wrapped lines. Keep
        // the full rectangle in the tile intersection check so no such glyph
        // disappears from the original-resolution export.
        var referenceInk = note.ReferenceBounds;
        return new Layout(note, allText, text, shadows, sourceBounds, scale,
            SourceBoundsFor(note, referenceInk));
    }

    private static FormattedText CreateFormattedText(MainWindow.TextNoteSnapshot note,
        string allText, double fontSize, double width, double height, double scale,
        double? shadowDistance)
    {
        // WPF line-break layout can otherwise shape an unbounded amount of text
        // before applying MaxTextHeight. Keep only the amount that could fit in
        // the rectangle at the chosen pixel size; editing keeps the full string.
        var visibleText = allText.Length > 200_000 ? allText[..200_000] : allText;
        var text = new FormattedText(visibleText, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily(note.Settings.FontFamilyName), FontStyles.Normal,
                note.Settings.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            fontSize, shadowDistance is null ? new SolidColorBrush(note.Settings.Color)
                : Brushes.Transparent, 1)
        {
            MaxTextWidth = width,
            MaxTextHeight = height,
            Trimming = TextTrimming.None
        };
        var offset = 0;
        foreach (var run in note.Runs)
        {
            if (run.Text.Length == 0 || offset >= visibleText.Length) continue;
            var count = Math.Min(run.Text.Length, visibleText.Length - offset);
            text.SetFontFamily(new FontFamily(run.FontFamilyName), offset, count);
            text.SetFontWeight(run.Bold ? FontWeights.Bold : FontWeights.Normal,
                offset, count);
            text.SetFontSize(Math.Max(.001, run.FontSizePixels * scale), offset, count);
            var brush = shadowDistance is null ? new SolidColorBrush(run.Color)
                : run.HasShadow && Math.Abs(run.ShadowSize - shadowDistance.Value) < .001
                    ? new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)) : Brushes.Transparent;
            text.SetForegroundBrush(brush, offset, count);
            offset += count;
        }
        return text;
    }

    private static AnnotationTile? Render(Work request, Layout[] layouts)
    {
        var region = new Rect(request.X, request.Y, request.Width, request.Height);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.PushClip(new RectangleGeometry(new Rect(0, 0,
                request.Width, request.Height)));
            foreach (var layout in layouts)
            {
                request.Token.ThrowIfCancellationRequested();
                if (!layout.InkBounds.IntersectsWith(region) ||
                    string.IsNullOrWhiteSpace(layout.AllText)) continue;
                var note = layout.Note;
                var origin = ToSource(note.ReferenceBounds.TopLeft, note);
                var xUnit = ToSource(new Point(note.ReferenceBounds.X + 1,
                    note.ReferenceBounds.Y), note);
                var yUnit = ToSource(new Point(note.ReferenceBounds.X,
                    note.ReferenceBounds.Y + 1), note);
                drawing.PushTransform(new MatrixTransform(new Matrix(
                    (xUnit.X - origin.X) / layout.LayoutScale,
                    (xUnit.Y - origin.Y) / layout.LayoutScale,
                    (yUnit.X - origin.X) / layout.LayoutScale,
                    (yUnit.Y - origin.Y) / layout.LayoutScale,
                    origin.X - request.X, origin.Y - request.Y)));
                drawing.PushClip(new RectangleGeometry(new Rect(0, 0,
                    note.ReferenceBounds.Width * layout.LayoutScale,
                    note.ReferenceBounds.Height * layout.LayoutScale)));
                foreach (var (distance, shadow) in layout.Shadows)
                    drawing.DrawText(shadow, new Point(distance * layout.LayoutScale,
                        distance * layout.LayoutScale));
                drawing.DrawText(layout.Text, new Point(0, 0));
                drawing.Pop();
                drawing.Pop();
            }
            drawing.Pop();
        }
        var bitmap = new RenderTargetBitmap(request.Width, request.Height, 96, 96,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[request.Width * request.Height * 4];
        bitmap.CopyPixels(pixels, request.Width * 4, 0);
        var occupied = false;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha == 0) continue;
            occupied = true;
            if (alpha == 255) continue;
            pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / alpha);
            pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / alpha);
            pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / alpha);
        }
        return occupied ? new AnnotationTile(request.X, request.Y,
            request.Width, request.Height, pixels) : null;
    }

    private static Rect SourceBoundsFor(MainWindow.TextNoteSnapshot note)
    {
        return SourceBoundsFor(note, note.ReferenceBounds);
    }

    private static Rect SourceBoundsFor(MainWindow.TextNoteSnapshot note, Rect bounds)
    {
        var corners = new[]
        {
            ToSource(bounds.TopLeft, note), ToSource(bounds.TopRight, note),
            ToSource(bounds.BottomLeft, note), ToSource(bounds.BottomRight, note)
        };
        var x0 = Math.Max(0, Math.Floor(corners.Min(p => p.X)));
        var y0 = Math.Max(0, Math.Floor(corners.Min(p => p.Y)));
        var x1 = Math.Min(note.SourceWidth, Math.Ceiling(corners.Max(p => p.X)));
        var y1 = Math.Min(note.SourceHeight, Math.Ceiling(corners.Max(p => p.Y)));
        return new Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    private static Point ToSource(Point point, MainWindow.TextNoteSnapshot note) =>
        note.ReferenceRotation switch
        {
            90 => new Point(point.Y, note.SourceHeight - point.X),
            180 => new Point(note.SourceWidth - point.X, note.SourceHeight - point.Y),
            270 => new Point(note.SourceWidth - point.Y, point.X),
            _ => point
        };

    public void Dispose()
    {
        lock (lifecycleGate)
        {
            if (disposed) return;
            disposed = true;
            work.CompleteAdding();
        }
        // The worker is a background thread and exits when the queue drains.
        // Never block the UI thread waiting for WPF raster cleanup.
    }
}
