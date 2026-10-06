using System.Globalization;
using System.Collections.Concurrent;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using TANGERINE_PhotoViewer.DefaultApps;

namespace TANGERINE_PhotoViewer;

public partial class MainWindow
{
    /// <summary>
    /// A text object keeps its own edit control and reference image rotation. The control
    /// is lightweight relative to the photograph, and export rasterizes it one 256-pixel
    /// source tile at a time without ever allocating a full-image bitmap.
    /// </summary>
    private sealed class TextNote
    {
        internal required RichTextBox Editor;
        internal Border? Frame;
        internal Ellipse[]? CornerHandles;
        internal Canvas? InverseFrame;
        internal Image[]? InverseEdges;
        internal Border? SelectionShadowPreview;
        internal System.Windows.Threading.DispatcherTimer? ShadowPreviewDelay;
        internal required Rect ReferenceBounds;
        internal required int ReferenceRotation;
        internal required TextToolSettings Settings;
        internal required double LayoutScale;
        internal required double FontPixels;
        internal required double FontLimitWidthFactor;
    }

    internal sealed record TextRunSnapshot(string Text, string FontFamilyName,
        Color Color, bool Bold, bool HasShadow, double ShadowSize,
        double FontSizePixels);

    internal sealed record TextNoteSnapshot(Rect ReferenceBounds, int ReferenceRotation,
        int SourceWidth, int SourceHeight, TextToolSettings Settings,
        IReadOnlyList<TextRunSnapshot> Runs);

    private readonly List<TextNote> textNotes = [];
    private TextToolSettings textToolSettings = TextToolSettings.Default;
    private TextNote? selectedTextNote;
    private TextNote? hoveredTextNote;
    private TextNote? draggedTextNote;
    private readonly System.Windows.Threading.DispatcherTimer inverseFrameDelay = new()
    {
        Interval = TimeSpan.FromMilliseconds(16)
    };
    private Point textFrameDragStart;
    private Rect textFrameOriginalBounds;
    private bool resizingTextFrame;
    private Point? textDragOrigin;
    private Border? textDragPreview;
    private bool installingTextNote;
    // Attached formatting follows a Run through WPF's rich-text split/merge
    // operations. A Run-keyed dictionary would retain deleted text indefinitely.
    private static readonly DependencyProperty RunShadowSizeProperty =
        DependencyProperty.RegisterAttached("RunShadowSize", typeof(double),
            typeof(MainWindow), new FrameworkPropertyMetadata(double.NaN,
                FrameworkPropertyMetadataOptions.Inherits));
    private readonly SemaphoreSlim textPreviewGate = new(1, 1);
    private int pendingTextPreviewWorkers;
    private TextNote? queuedTextShadowNote;

    private bool HasSelectedText => selectedTextNote?.Editor.Selection.IsEmpty == false;

    private bool IsTextEditorEvent(RoutedEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject current) return false;
        while (current is not null)
        {
            if (current is RichTextBox editor && textNotes.Any(note => ReferenceEquals(note.Editor, editor)))
                return true;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    private bool IsTextEditorFocused()
    {
        if (Keyboard.FocusedElement is not DependencyObject current) return false;
        while (current is not null)
        {
            if (current is RichTextBox editor && textNotes.Any(note => ReferenceEquals(note.Editor, editor)))
                return true;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    private void StartTextNoteDrag(Point canvasPoint)
    {
        if (!double.IsFinite(canvasPoint.X + canvasPoint.Y)) return;
        selectedTextNote = null;
        UpdateTextSelectionMenu();
        textDragOrigin = canvasPoint;
        drawingNote = true;
        textDragPreview = new Border
        {
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1 / Math.Max(scale, .001)),
            Background = new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)),
            IsHitTestVisible = false
        };
        Panel.SetZIndex(textDragPreview, 2000);
        NotesCanvas.Children.Add(textDragPreview);
        MoveTextNoteDrag(canvasPoint);
        UpdateNoteStatus();
    }

    private void MoveTextNoteDrag(Point canvasPoint)
    {
        if (textDragOrigin is null || textDragPreview is null) return;
        var displayWidth = rotation is 90 or 270 ? imageHeight : imageWidth;
        var displayHeight = rotation is 90 or 270 ? imageWidth : imageHeight;
        var p0 = ClampDisplayPoint(textDragOrigin.Value, displayWidth, displayHeight);
        var p1 = ClampDisplayPoint(canvasPoint, displayWidth, displayHeight);
        var rect = new Rect(p0, p1);
        Canvas.SetLeft(textDragPreview, rect.X);
        Canvas.SetTop(textDragPreview, rect.Y);
        textDragPreview.Width = rect.Width;
        textDragPreview.Height = rect.Height;
    }

    private void FinishTextNoteDrag(Point canvasPoint)
    {
        drawingNote = false;
        if (textDragOrigin is null) return;
        var origin = textDragOrigin.Value;
        textDragOrigin = null;
        if (textDragPreview is not null) NotesCanvas.Children.Remove(textDragPreview);
        textDragPreview = null;
        var displayWidth = rotation is 90 or 270 ? imageHeight : imageWidth;
        var displayHeight = rotation is 90 or 270 ? imageWidth : imageHeight;
        var p0 = ClampDisplayPoint(origin, displayWidth, displayHeight);
        var p1 = ClampDisplayPoint(canvasPoint, displayWidth, displayHeight);
        if (Math.Abs(p0.X - p1.X) < 1 || Math.Abs(p0.Y - p1.Y) < 1)
        {
            ShowTextStatus("TextSpaceInvalid");
            return;
        }
        var bounds = new Rect(p0, p1);
        var pixelFontSize = bounds.Height * textToolSettings.HeightFraction;
        // Normalize enormous source-space rectangles before asking WPF to lay
        // out editable text. Canvas transforms restore the exact image geometry.
        var layoutScale = Math.Min(1, Math.Min(2048 / Math.Max(1, bounds.Width),
            2048 / Math.Max(1, bounds.Height)));
        var layoutFontSize = pixelFontSize * layoutScale;
        var layoutWidth = bounds.Width * layoutScale;
        var layoutHeight = bounds.Height * layoutScale;
        if (!CanFitDefaultText(layoutWidth, layoutFontSize, textToolSettings))
        {
            ShowTextStatus("TextRectangleTooSmall");
            return;
        }
        var placeholder = FittingPlaceholder(layoutWidth, layoutFontSize, textToolSettings);
        if (placeholder.Length == 0)
        {
            ShowTextStatus("TextRectangleTooSmall");
            return;
        }

        try
        {
            var document = new FlowDocument(new Paragraph(new Run(placeholder)))
            {
                PagePadding = new Thickness(0),
                ColumnWidth = double.PositiveInfinity,
                FontFamily = new FontFamily(textToolSettings.FontFamilyName),
                FontSize = layoutFontSize,
                FontWeight = textToolSettings.Bold ? FontWeights.Bold : FontWeights.Normal,
                Foreground = new SolidColorBrush(textToolSettings.Color),
                Background = Brushes.Transparent
            };
            var paragraph = (Paragraph)document.Blocks.FirstBlock!;
            paragraph.Margin = new Thickness(0);
            // Automatic line height allows a later range-specific font size to
            // expand its own line without clipping against the original size.
            paragraph.LineHeight = double.NaN;
            var editor = new RichTextBox(document)
            {
                Width = layoutWidth,
                Height = layoutHeight,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(textToolSettings.Color),
                FontSize = layoutFontSize,
                FontFamily = new FontFamily(textToolSettings.FontFamilyName),
                FontWeight = textToolSettings.Bold ? FontWeights.Bold : FontWeights.Normal,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                ClipToBounds = true,
                AcceptsTab = false,
                IsUndoEnabled = true
            };
            ApplyEditorShadow(editor, textToolSettings);
            var note = new TextNote
            {
                Editor = editor,
                ReferenceBounds = bounds,
                ReferenceRotation = rotation,
                Settings = textToolSettings,
                LayoutScale = layoutScale,
                FontPixels = pixelFontSize,
                // This is the minimum width for the first placeholder glyph;
                // rectangles that can show that glyph remain editable.
                FontLimitWidthFactor = MeasurePrefix("T", 1, textToolSettings)
            };
            editor.SelectionChanged += (_, _) =>
            {
                selectedTextNote = note;
                UpdateTextSelectionMenu();
            };
            editor.GotKeyboardFocus += (_, _) =>
            {
                selectedTextNote = note;
                UpdateTextSelectionMenu();
            };
            editor.TextChanged += (_, _) =>
            {
                if (installingTextNote) return;
                notesVersion++;
                noteStatusResult = false;
                note.ShadowPreviewDelay?.Stop();
                note.ShadowPreviewDelay?.Start();
                RefreshMenu();
                UpdateNoteStatus();
            };
            note.ShadowPreviewDelay = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(180)
            };
            note.ShadowPreviewDelay.Tick += (_, _) =>
            {
                note.ShadowPreviewDelay.Stop();
                QueueSelectedShadowPreview(note);
            };
            installingTextNote = true;
            textNotes.Add(note);
            NotesCanvas.Children.Add(editor);
            CreateTextNoteFrame(note);
            Panel.SetZIndex(editor, 1000);
            PositionTextNote(note);
            selectedTextNote = note;
            notesVersion++;
            noteStatusResult = false;
            installingTextNote = false;
            editor.Focus();
            editor.SelectAll();
            UpdateTextSelectionMenu();
            RefreshMenu();
            UpdateNoteStatus();
        }
        catch (Exception ex)
        {
            installingTextNote = false;
            ShowNotesError(new StageException("TEXT0001", LanguageManager.Get("TextRenderingFailed"), ex)); // TEXT0001
        }
    }

    private static Point ClampDisplayPoint(Point point, double width, double height) =>
        new(Math.Clamp(point.X, 0, width), Math.Clamp(point.Y, 0, height));

    private void ShowTextStatus(string key)
    {
        EditStatusLabel.Visibility = Visibility.Visible;
        EditStatusLabel.Content = LanguageManager.Get(key);
        noteStatusResult = true;
    }

    private static bool CanFitDefaultText(double width, double fontSize, TextToolSettings settings) =>
        fontSize >= 1 && MeasurePrefix("T", fontSize, settings) <= width;

    private static string FittingPlaceholder(double width, double fontSize, TextToolSettings settings)
    {
        var sample = LanguageManager.Get("TextPlaceholder");
        for (var count = sample.Length; count > 0; count--)
            if (MeasurePrefix(sample[..count], fontSize, settings) <= width)
                return sample[..count];
        return string.Empty;
    }

    private static double MeasurePrefix(string text, double fontSize, TextToolSettings settings)
    {
        var value = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily(settings.FontFamilyName), FontStyles.Normal,
                settings.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            fontSize, Brushes.White, 1);
        return value.WidthIncludingTrailingWhitespace;
    }

    private void OnTextModeEntered()
    {
        UpdateTextEditorsInteractivity();
        ApplyTextToolAppearance();
        UpdateTextSelectionMenu();
    }

    private void UpdateTextEditorsInteractivity()
    {
        var canEdit = editingNotes && noteTool == NoteTool.Text;
        foreach (var note in textNotes) note.Editor.IsHitTestVisible = canEdit;
        if (!canEdit)
        {
            SetHoveredTextNote(null);
            NotesCanvas.Cursor = null;
        }
    }

    private void CancelTextNoteEditing()
    {
        textDragOrigin = null;
        if (textDragPreview is not null) NotesCanvas.Children.Remove(textDragPreview);
        textDragPreview = null;
        draggedTextNote = null;
        SetHoveredTextNote(null);
        NotesCanvas.Cursor = null;
        selectedTextNote = null;
        foreach (var note in textNotes) note.Editor.IsHitTestVisible = false;
        UpdateTextSelectionMenu();
    }

    private void ResetTextNotes()
    {
        CancelTextNoteEditing();
        queuedTextShadowNote = null;
        foreach (var note in textNotes)
        {
            note.ShadowPreviewDelay?.Stop();
            NotesCanvas.Children.Remove(note.Editor);
            if (note.Frame is not null) NotesCanvas.Children.Remove(note.Frame);
            if (note.InverseFrame is not null) NotesCanvas.Children.Remove(note.InverseFrame);
            if (note.SelectionShadowPreview is not null)
                NotesCanvas.Children.Remove(note.SelectionShadowPreview);
        }
        inverseFrameDelay.Stop();
        textNotes.Clear();
    }

    private void UpdateTextSelectionMenu()
    {
        if (TextSettingsItem is null) return;
        TextSettingsItem.Visibility = editingNotes && noteTool == NoteTool.Text && HasSelectedText
            ? Visibility.Visible : Visibility.Collapsed;
        TextFontSizeItem.Visibility = editingNotes && noteTool == NoteTool.Text && selectedTextNote is not null
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyTextToolAppearance()
    {
        if (TextItem is null) return;
        var color = textToolSettings.Color;
        var inverse = Color.FromRgb((byte)(255 - color.R), (byte)(255 - color.G),
            (byte)(255 - color.B));
        TextItem.Foreground = new SolidColorBrush(color);
        TextItem.Background = new SolidColorBrush(inverse);
    }

    private void OpenTextToolOptions()
    {
        try
        {
            var window = new TextToolOptionsWindow(textToolSettings, false) { Owner = this };
            if (window.ShowDialog() != true) return;
            textToolSettings = window.SelectedSettings;
            ApplyTextToolAppearance();
            UpdateNoteStatus();
        }
        catch (Exception ex)
        {
            ShowNotesError(new StageException("TEXT0002", LanguageManager.Get("TextRenderingFailed"), ex)); // TEXT0002
        }
    }

    private void OpenSelectedTextOptions()
    {
        var note = selectedTextNote;
        if (note is null || note.Editor.Selection.IsEmpty) return;
        try
        {
            var selection = note.Editor.Selection;
            var familyValue = selection.GetPropertyValue(TextElement.FontFamilyProperty);
            var weightValue = selection.GetPropertyValue(TextElement.FontWeightProperty);
            var foregroundValue = selection.GetPropertyValue(TextElement.ForegroundProperty);
            var current = note.Settings with
            {
                FontFamilyName = familyValue is FontFamily family ? family.Source : note.Settings.FontFamilyName,
                Bold = weightValue is FontWeight weight && weight == FontWeights.Bold,
                Color = foregroundValue is SolidColorBrush brush ? brush.Color : note.Settings.Color
            };
            var sizeValue = selection.GetPropertyValue(TextElement.FontSizeProperty);
            var maximum = MaximumTextFontPixels(note, note.ReferenceBounds);
            if (maximum < 1) { ShowTextStatus("TextRectangleTooSmall"); return; }
            var currentPixels = sizeValue is double selectedSize && double.IsFinite(selectedSize)
                ? selectedSize / Math.Max(.001, note.LayoutScale) : note.FontPixels;
            var window = new TextToolOptionsWindow(current, true,
                Math.Clamp(currentPixels, 1, maximum), maximum) { Owner = this };
            if (window.ShowDialog() != true) return;
            var chosen = window.SelectedSettings;
            // ApplyPropertyValue splits runs at the selection boundaries, so
            // neighboring characters retain their font size and other styles.
            selection.ApplyPropertyValue(TextElement.FontFamilyProperty,
                new FontFamily(chosen.FontFamilyName));
            selection.ApplyPropertyValue(TextElement.ForegroundProperty,
                new SolidColorBrush(chosen.Color));
            selection.ApplyPropertyValue(TextElement.FontWeightProperty,
                chosen.Bold ? FontWeights.Bold : FontWeights.Normal);
            selection.ApplyPropertyValue(TextElement.FontSizeProperty,
                window.SelectedFontPixels * note.LayoutScale);
            foreach (var block in note.Editor.Document.Blocks)
                if (block is Paragraph paragraph) paragraph.LineHeight = double.NaN;
            // WPF stores font and color on text ranges, while drop shadows are
            // attached to visual elements. Apply a stable per-run shadow marker
            // to the runs produced by range formatting for tile export.
            foreach (var run in RunsIntersectingSelection(note.Editor, selection))
                run.SetValue(RunShadowSizeProperty,
                    chosen.HasShadow ? chosen.ShadowSize : 0d);
            // The editor-level effect only applies to the whole rectangle. Keep
            // selected-range shadows in the per-run export model, so neighboring
            // text never acquires a shadow that the user did not request.
            QueueSelectedShadowPreview(note);
            notesVersion++;
            noteStatusResult = false;
            RefreshMenu();
            UpdateNoteStatus();
            note.Editor.Focus();
        }
        catch (Exception ex)
        {
            ShowNotesError(new StageException("TEXT0003", LanguageManager.Get("TextRenderingFailed"), ex)); // TEXT0003
        }
    }

    /// <summary>
    /// Font size belongs to the whole text rectangle. The upper bound is the
    /// smaller of its usable height and the width needed for one visible glyph.
    /// Existing run colors and font families remain intact when only size changes.
    /// </summary>
    private void OpenTextFontSizeOptions()
    {
        var note = selectedTextNote;
        if (note is null) return;
        try
        {
            var bounds = note.ReferenceBounds;
            var maximum = MaximumTextFontPixels(note, bounds);
            if (maximum < 1) { ShowTextStatus("TextRectangleTooSmall"); return; }
            var window = new TextFontSizeWindow(Math.Clamp(note.FontPixels, 1, maximum), maximum)
                { Owner = this };
            if (window.ShowDialog() != true) return;
            note.FontPixels = window.SelectedFontPixels;
            ApplyTextNoteLayout(note);
            notesVersion++;
            noteStatusResult = false;
            note.ShadowPreviewDelay?.Stop();
            note.ShadowPreviewDelay?.Start();
            RefreshMenu();
            UpdateNoteStatus();
        }
        catch (Exception ex)
        {
            ShowNotesError(new StageException("TEXT0006", LanguageManager.Get("TextRenderingFailed"), ex)); // TEXT0006
        }
    }

    private static double MaximumTextFontPixels(TextNote note, Rect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return 0;
        return Math.Max(0, Math.Min(bounds.Height,
            bounds.Width / Math.Max(.001, note.FontLimitWidthFactor)));
    }

    private static void ApplyTextNoteLayout(TextNote note)
    {
        var oldScale = Math.Max(.001, note.LayoutScale);
        // Local Run values represent user-selected sizes. Preserve those source
        // pixels when a rectangle is resized and its UI layout scale changes.
        var chosenRunSizes = note.Editor.Document.Blocks.OfType<Paragraph>()
            .SelectMany(paragraph => paragraph.Inlines.Cast<Inline>())
            .SelectMany(DescendantRuns)
            .Where(run => run.ReadLocalValue(TextElement.FontSizeProperty)
                != DependencyProperty.UnsetValue ||
                Math.Abs(run.FontSize - note.Editor.Document.FontSize) > .001)
            .Select(run => (Run: run, Pixels: run.FontSize / oldScale))
            .ToArray();
        var layoutScale = Math.Min(1, Math.Min(2048 / Math.Max(1, note.ReferenceBounds.Width),
            2048 / Math.Max(1, note.ReferenceBounds.Height)));
        note.LayoutScale = layoutScale;
        // Keep the chosen source-pixel size even for a very large rectangle.
        // Flooring the normalized WPF size at one would enlarge small text on
        // a heavily downscaled editor and make the saved image disagree.
        var fontSize = Math.Max(.001, note.FontPixels * layoutScale);
        note.Editor.Document.FontSize = fontSize;
        note.Editor.FontSize = fontSize;
        var maximum = Math.Max(1, MaximumTextFontPixels(note, note.ReferenceBounds));
        foreach (var (run, pixels) in chosenRunSizes)
            run.FontSize = Math.Max(.001, Math.Clamp(pixels, 1, maximum) * layoutScale);
        foreach (var paragraph in note.Editor.Document.Blocks.OfType<Paragraph>())
            paragraph.LineHeight = double.NaN;
    }

    private static IEnumerable<Run> RunsIntersectingSelection(RichTextBox editor,
        TextSelection selection)
    {
        foreach (var block in editor.Document.Blocks)
        {
            if (block is not Paragraph paragraph) continue;
            foreach (var inline in paragraph.Inlines)
                foreach (var run in DescendantRuns(inline))
                    if (run.ContentStart.CompareTo(selection.End) < 0 &&
                        run.ContentEnd.CompareTo(selection.Start) > 0)
                        yield return run;
        }
    }

    private static IEnumerable<Run> DescendantRuns(Inline inline)
    {
        if (inline is Run run) yield return run;
        else if (inline is Span span)
            foreach (var child in span.Inlines)
                foreach (var nested in DescendantRuns(child))
                    yield return nested;
    }

    private static void ApplyEditorShadow(RichTextBox editor, TextToolSettings settings)
    {
        editor.Effect = settings.HasShadow && settings.ShadowSize > 0
            ? new DropShadowEffect
            {
                Color = Colors.Black,
                Opacity = .55,
                ShadowDepth = settings.ShadowSize,
                BlurRadius = 0,
                Direction = 315
            }
            : null;
    }

    /// <summary>
    /// Rebuilds the selected-range shadow on a separate STA renderer and swaps a
    /// transparent tile preview on the UI thread. Revisions prevent a slow older
    /// render from replacing a newer text edit. The UI preview may be downsampled
    /// to at most 2048 pixels per side; export rasterizes original-image pixels.
    /// </summary>
    private void QueueSelectedShadowPreview(TextNote note)
    {
        if (!textNotes.Contains(note)) return;
        TextNoteSnapshot snapshot;
        try { snapshot = SnapshotTextNote(note); }
        catch (Exception ex)
        {
            ShowNotesError(new StageException("TEXT0005",
                LanguageManager.Get("TextRenderingFailed"), ex)); // TEXT0005
            return;
        }
        var mixedShadow = snapshot.Runs.Any(run => run.HasShadow != note.Settings.HasShadow ||
            Math.Abs(run.ShadowSize - note.Settings.ShadowSize) > .001);
        if (!mixedShadow)
        {
            if (note.SelectionShadowPreview is not null)
            {
                NotesCanvas.Children.Remove(note.SelectionShadowPreview);
                note.SelectionShadowPreview = null;
            }
            ApplyEditorShadow(note.Editor, note.Settings);
            return;
        }
        note.Editor.Effect = null;
        // Preview is optional for extremely large dragged rectangles; export is
        // still tiled at the original resolution. This cap prevents one giant UI
        // bitmap from exhausting memory during ordinary typing.
        var previewScale = Math.Min(1, Math.Min(2048 / Math.Max(1, note.ReferenceBounds.Width),
            2048 / Math.Max(1, note.ReferenceBounds.Height)));
        var width = Math.Max(1, (int)Math.Ceiling(note.ReferenceBounds.Width * previewScale));
        var height = Math.Max(1, (int)Math.Ceiling(note.ReferenceBounds.Height * previewScale));
        var pixels = (long)width * height;
        if (pixels > 4_194_304)
        {
            var adjust = Math.Sqrt(4_194_304d / pixels);
            previewScale *= adjust;
            width = Math.Max(1, (int)Math.Floor(width * adjust));
            height = Math.Max(1, (int)Math.Floor(height * adjust));
        }
        var revision = notesVersion;
        // The debounce timer and one pending render bound simultaneous STA work
        // even if the user types rapidly while a previous image is rendering.
        if (Interlocked.Exchange(ref pendingTextPreviewWorkers, 1) != 0)
        {
            queuedTextShadowNote = note;
            return;
        }
        var renderThread = new Thread(() =>
        {
            var acquired = false;
            try
            {
                textPreviewGate.Wait();
                acquired = true;
                if (revision != Interlocked.Read(ref notesVersion)) return;
                var bitmap = RenderSelectedShadowPreview(snapshot, width, height, previewScale);
                bitmap.Freeze();
                Dispatcher.BeginInvoke(() =>
                {
                    if (revision != notesVersion || !textNotes.Contains(note)) return;
                    if (note.SelectionShadowPreview is null)
                    {
                        note.SelectionShadowPreview = new Border { IsHitTestVisible = false,
                            ClipToBounds = true };
                        NotesCanvas.Children.Add(note.SelectionShadowPreview);
                        Panel.SetZIndex(note.SelectionShadowPreview, 999);
                    }
                    note.SelectionShadowPreview.Background = new ImageBrush(bitmap)
                    {
                        Stretch = Stretch.Fill
                    };
                    PositionTextNote(note);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (revision == notesVersion && textNotes.Contains(note))
                        ShowNotesError(new StageException("TEXT0005",
                            LanguageManager.Get("TextRenderingFailed"), ex)); // TEXT0005
                });
            }
            finally
            {
                if (acquired) textPreviewGate.Release();
                Dispatcher.BeginInvoke(() =>
                {
                    Interlocked.Exchange(ref pendingTextPreviewWorkers, 0);
                    var queued = queuedTextShadowNote;
                    queuedTextShadowNote = null;
                    if (queued is not null && textNotes.Contains(queued))
                        QueueSelectedShadowPreview(queued);
                });
            }
        }) { IsBackground = true, Name = "Text shadow preview renderer" };
        try
        {
            renderThread.SetApartmentState(ApartmentState.STA);
            renderThread.Start();
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref pendingTextPreviewWorkers, 0);
            ShowNotesError(new StageException("TEXT0005",
                LanguageManager.Get("TextRenderingFailed"), ex)); // TEXT0005
        }
    }

    private static RenderTargetBitmap RenderSelectedShadowPreview(TextNoteSnapshot note,
        int width, int height, double previewScale)
    {
        var visual = new DrawingVisual();
        var allText = string.Concat(note.Runs.Select(run => run.Text));
        var fontSize = note.ReferenceBounds.Height * note.Settings.HeightFraction * previewScale;
        var layout = new FormattedText(allText, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily(note.Settings.FontFamilyName), FontStyles.Normal,
                note.Settings.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            fontSize, Brushes.Transparent, 1)
        {
            MaxTextWidth = Math.Max(1, width),
            MaxTextHeight = Math.Max(1, height)
        };
        var character = 0;
        foreach (var run in note.Runs)
        {
            if (run.Text.Length == 0) continue;
            layout.SetFontFamily(new FontFamily(run.FontFamilyName), character, run.Text.Length);
            layout.SetFontWeight(run.Bold ? FontWeights.Bold : FontWeights.Normal,
                character, run.Text.Length);
            layout.SetFontSize(Math.Max(.001, run.FontSizePixels * previewScale),
                character, run.Text.Length);
            character += run.Text.Length;
        }
        using (var drawing = visual.RenderOpen())
        {
            drawing.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));
            foreach (var distance in note.Runs.Where(run => run.HasShadow && run.ShadowSize > 0)
                .Select(run => run.ShadowSize).Distinct())
            {
                character = 0;
                foreach (var run in note.Runs)
                {
                    if (run.Text.Length == 0) continue;
                    layout.SetForegroundBrush(run.HasShadow && run.ShadowSize == distance
                        ? new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)) : Brushes.Transparent,
                        character, run.Text.Length);
                    character += run.Text.Length;
                }
                drawing.DrawText(layout, new Point(distance * previewScale,
                    distance * previewScale));
            }
            drawing.Pop();
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private void UpdateTextNoteGeometry()
    {
        foreach (var note in textNotes) PositionTextNote(note);
    }

    /// <summary>
    /// The frame is a visual guide only. Pointer decisions use image coordinates,
    /// which keeps the resize and move hit area stable under zoom and rotation.
    /// </summary>
    private void CreateTextNoteFrame(TextNote note)
    {
        var frame = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(92, 0, 0, 0)),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        // A transparent canvas holds only four narrow images. Each image is
        // generated from the exact displayed pixels beneath its border strip;
        // no full-photo bitmap is ever allocated for hover feedback.
        var inverseFrame = new Canvas { IsHitTestVisible = false,
            Visibility = Visibility.Collapsed };
        var inverseEdges = new Image[4];
        for (var index = 0; index < inverseEdges.Length; index++)
        {
            inverseEdges[index] = new Image { Stretch = Stretch.Fill,
                IsHitTestVisible = false, SnapsToDevicePixels = true };
            inverseFrame.Children.Add(inverseEdges[index]);
        }
        var handles = new Ellipse[4];
        var canvas = new Canvas { IsHitTestVisible = false };
        for (var index = 0; index < handles.Length; index++)
        {
            handles[index] = new Ellipse { Fill = Brushes.White, Stroke = Brushes.Black,
                StrokeThickness = .5 / Math.Max(scale, .001), IsHitTestVisible = false };
            canvas.Children.Add(handles[index]);
        }
        frame.Child = canvas;
        note.Frame = frame;
        note.CornerHandles = handles;
        note.InverseFrame = inverseFrame;
        note.InverseEdges = inverseEdges;
        NotesCanvas.Children.Add(frame);
        NotesCanvas.Children.Add(inverseFrame);
        Panel.SetZIndex(frame, 1500);
        Panel.SetZIndex(inverseFrame, 1501);
        inverseFrameDelay.Tick -= InverseFrameDelay_Tick;
        inverseFrameDelay.Tick += InverseFrameDelay_Tick;
        PositionTextNoteFrame(note);
    }

    private void PositionTextNoteFrame(TextNote note)
    {
        if (note.Frame is null || note.CornerHandles is null) return;
        var bounds = note.ReferenceBounds;
        var p0 = ReferenceToCurrent(bounds.TopLeft, note.ReferenceRotation);
        var px = ReferenceToCurrent(bounds.TopRight, note.ReferenceRotation);
        var py = ReferenceToCurrent(bounds.BottomLeft, note.ReferenceRotation);
        // A giant image-space rectangle still gets a bounded WPF visual; the
        // same transform used by its editor restores its displayed position.
        note.Frame.Width = bounds.Width * note.LayoutScale;
        note.Frame.Height = bounds.Height * note.LayoutScale;
        Canvas.SetLeft(note.Frame, p0.X);
        Canvas.SetTop(note.Frame, p0.Y);
        note.Frame.RenderTransform = new MatrixTransform(new Matrix(
            (px.X - p0.X) / (bounds.Width * note.LayoutScale),
            (px.Y - p0.Y) / (bounds.Width * note.LayoutScale),
            (py.X - p0.X) / (bounds.Height * note.LayoutScale),
            (py.Y - p0.Y) / (bounds.Height * note.LayoutScale), 0, 0));
        // The pixel-inverted overlay is sized to the visible viewport by its
        // renderer. Never give this canvas the dimensions of a huge source image.
        note.InverseFrame!.Visibility = Visibility.Collapsed;
        var radius = SystemParameters.WorkArea.Width * .0025 *
            note.LayoutScale / Math.Max(scale, .001);
        var stroke = note.LayoutScale / Math.Max(scale, .001);
        var points = new[] { bounds.TopLeft, bounds.TopRight, bounds.BottomLeft, bounds.BottomRight };
        for (var index = 0; index < 4; index++)
        {
            var handle = note.CornerHandles[index];
            handle.Width = handle.Height = radius * 2;
            handle.StrokeThickness = stroke;
            Canvas.SetLeft(handle, (points[index].X - bounds.X) * note.LayoutScale - radius);
            Canvas.SetTop(handle, (points[index].Y - bounds.Y) * note.LayoutScale - radius);
        }
        if (ReferenceEquals(hoveredTextNote, note)) QueueInverseFrameRefresh();
    }

    private void SetHoveredTextNote(TextNote? note)
    {
        if (ReferenceEquals(hoveredTextNote, note)) return;
        if (hoveredTextNote?.Frame is { } oldFrame) oldFrame.Visibility = Visibility.Collapsed;
        if (hoveredTextNote?.InverseFrame is { } oldInverse)
            oldInverse.Visibility = Visibility.Collapsed;
        hoveredTextNote = note;
        if (note?.Frame is { } frame) frame.Visibility = Visibility.Visible;
        if (note is not null) QueueInverseFrameRefresh();
        else inverseFrameDelay.Stop();
    }

    /// <summary>
    /// Batches the expensive displayed-pixel readback. Moving a pointer within
    /// one rectangle does not regenerate the unchanged strips on every event.
    /// Geometry and region-render changes request a fresh set instead.
    /// </summary>
    private void QueueInverseFrameRefresh()
    {
        if (hoveredTextNote is null) return;
        inverseFrameDelay.Stop();
        inverseFrameDelay.Start();
    }

    private void InverseFrameDelay_Tick(object? sender, EventArgs e)
    {
        inverseFrameDelay.Stop();
        var note = hoveredTextNote;
        if (note is null || !textNotes.Contains(note)) return;
        try { RenderInverseFrame(note); }
        catch (Exception ex)
        {
            if (note.InverseFrame is not null) note.InverseFrame.Visibility = Visibility.Collapsed;
            ShowNotesError(new StageException("TEXT0008",
                LanguageManager.Get("TextRenderingFailed"), ex)); // TEXT0008
        }
    }

    /// <summary>
    /// Reads only the four visible one-pixel strips from the already displayed
    /// photo. The strips are clipped to the scroll viewport, keeping allocations
    /// independent of the original image dimensions, even for huge photographs.
    /// </summary>
    private void RenderInverseFrame(TextNote note)
    {
        if (note.InverseFrame is null || note.InverseEdges is null ||
            note.ReferenceBounds.Width <= 0 || note.ReferenceBounds.Height <= 0) return;
        var frame = note.InverseFrame;
        var dpi = VisualTreeHelper.GetDpi(Viewer);
        var displayScale = Math.Max(.001, scale * Math.Max(dpi.DpiScaleX, dpi.DpiScaleY));
        var pixel = 1 / displayScale;
        var bounds = note.ReferenceBounds;
        var corners = new[]
        {
            ReferenceToCurrent(bounds.TopLeft, note.ReferenceRotation),
            ReferenceToCurrent(bounds.TopRight, note.ReferenceRotation),
            ReferenceToCurrent(bounds.BottomLeft, note.ReferenceRotation),
            ReferenceToCurrent(bounds.BottomRight, note.ReferenceRotation)
        };
        var left = corners.Min(point => point.X);
        var top = corners.Min(point => point.Y);
        var right = corners.Max(point => point.X);
        var bottom = corners.Max(point => point.Y);
        var viewport = new Rect(Viewer.HorizontalOffset / Math.Max(scale, .001),
            Viewer.VerticalOffset / Math.Max(scale, .001),
            Viewer.ViewportWidth / Math.Max(scale, .001),
            Viewer.ViewportHeight / Math.Max(scale, .001));
        var visibleFrame = Rect.Intersect(new Rect(left, top, right - left, bottom - top), viewport);
        if (visibleFrame.IsEmpty)
        {
            frame.Visibility = Visibility.Collapsed;
            return;
        }
        frame.Width = visibleFrame.Width;
        frame.Height = visibleFrame.Height;
        Canvas.SetLeft(frame, visibleFrame.X);
        Canvas.SetTop(frame, visibleFrame.Y);
        var strips = new[]
        {
            new Rect(left, top, Math.Max(0, right - left), Math.Min(pixel, bottom - top)),
            new Rect(left, Math.Max(top, bottom - pixel), Math.Max(0, right - left),
                Math.Min(pixel, bottom - top)),
            new Rect(left, top, Math.Min(pixel, right - left), Math.Max(0, bottom - top)),
            new Rect(Math.Max(left, right - pixel), top, Math.Min(pixel, right - left),
                Math.Max(0, bottom - top))
        };
        var previousVisibility = NotesCanvas.Visibility;
        try
        {
            // Hiding the annotation layer during readback prevents the frame
            // itself and its dark fill from becoming part of the source pixels.
            NotesCanvas.Visibility = Visibility.Hidden;
            for (var index = 0; index < strips.Length; index++)
            {
                var clipped = Rect.Intersect(strips[index], viewport);
                var edge = note.InverseEdges[index];
                if (clipped.IsEmpty || clipped.Width <= 0 || clipped.Height <= 0)
                {
                    edge.Visibility = Visibility.Collapsed;
                    continue;
                }
                var width = Math.Max(1, (int)Math.Ceiling(clipped.Width * displayScale));
                var height = Math.Max(1, (int)Math.Ceiling(clipped.Height * displayScale));
                var surfacePixels = new Rect(clipped.X * scale, clipped.Y * scale,
                    clipped.Width * scale, clipped.Height * scale);
                var brush = new VisualBrush(Surface)
                {
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = surfacePixels,
                    ViewportUnits = BrushMappingMode.Absolute,
                    Viewport = new Rect(0, 0, width, height),
                    Stretch = Stretch.Fill
                };
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                    drawing.DrawRectangle(brush, null, new Rect(0, 0, width, height));
                var captured = new RenderTargetBitmap(width, height, 96, 96,
                    PixelFormats.Pbgra32);
                captured.Render(visual);
                var pixels = new byte[checked(width * height * 4)];
                captured.CopyPixels(pixels, width * 4, 0);
                for (var offset = 0; offset < pixels.Length; offset += 4)
                {
                    pixels[offset] = (byte)(255 - pixels[offset]);
                    pixels[offset + 1] = (byte)(255 - pixels[offset + 1]);
                    pixels[offset + 2] = (byte)(255 - pixels[offset + 2]);
                    pixels[offset + 3] = 255;
                }
                var inverse = BitmapSource.Create(width, height, 96, 96,
                    PixelFormats.Bgra32, null, pixels, width * 4);
                inverse.Freeze();
                edge.Source = inverse;
                edge.Width = clipped.Width;
                edge.Height = clipped.Height;
                Canvas.SetLeft(edge, clipped.X - visibleFrame.X);
                Canvas.SetTop(edge, clipped.Y - visibleFrame.Y);
                edge.Visibility = Visibility.Visible;
            }
        }
        finally { NotesCanvas.Visibility = previousVisibility; }
        if (ReferenceEquals(hoveredTextNote, note)) frame.Visibility = Visibility.Visible;
    }

    private Point CurrentToReference(Point current, TextNote note)
    {
        var sourcePoint = ToSourcePoint(current, rotation, imageWidth, imageHeight);
        return FromSourcePoint(sourcePoint, note.ReferenceRotation, imageWidth, imageHeight);
    }

    private TextNote? FindTextFrameAt(Point current, out bool resize)
    {
        resize = false;
        if (!editingNotes || noteTool != NoteTool.Text) return null;
        foreach (var note in textNotes.AsEnumerable().Reverse())
        {
            var point = CurrentToReference(current, note);
            var bounds = note.ReferenceBounds;
            var tolerance = SystemParameters.WorkArea.Width * .005 /
                Math.Max(scale, .001);
            var nearRight = Math.Abs(point.X - bounds.Right) <= tolerance;
            var nearBottom = Math.Abs(point.Y - bounds.Bottom) <= tolerance;
            if (nearRight && nearBottom) { resize = true; return note; }
            var nearLeft = Math.Abs(point.X - bounds.Left) <= tolerance;
            var nearTop = Math.Abs(point.Y - bounds.Top) <= tolerance;
            var onHorizontal = (nearTop || nearBottom) &&
                point.X >= bounds.Left - tolerance && point.X <= bounds.Right + tolerance;
            var onVertical = (nearLeft || nearRight) &&
                point.Y >= bounds.Top - tolerance && point.Y <= bounds.Bottom + tolerance;
            if (onHorizontal || onVertical) return note;
        }
        return null;
    }

    private TextNote? FindTextFrameInside(Point current)
    {
        if (!editingNotes || noteTool != NoteTool.Text) return null;
        foreach (var note in textNotes.AsEnumerable().Reverse())
            if (note.ReferenceBounds.Contains(CurrentToReference(current, note)))
                return note;
        return null;
    }

    private bool IsTextFrameHandleEvent(MouseButtonEventArgs e) =>
        FindTextFrameAt(e.GetPosition(NotesCanvas), out _) is not null;

    private void UpdateTextFrameHover(Point current)
    {
        if (draggedTextNote is not null) return;
        var hit = FindTextFrameAt(current, out var resize);
        var note = hit ?? FindTextFrameInside(current);
        SetHoveredTextNote(note);
        if (editingNotes && noteTool == NoteTool.Text)
            NotesCanvas.Cursor = hit is null ? null :
                resize ? Cursors.SizeNWSE : Cursors.SizeAll;
    }

    private void ClearTextFrameHover()
    {
        if (draggedTextNote is not null) return;
        SetHoveredTextNote(null);
        NotesCanvas.Cursor = null;
    }


    private bool TryBeginTextFrameDrag(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || Keyboard.IsKeyDown(Key.Space)) return false;
        var current = e.GetPosition(NotesCanvas);
        var note = FindTextFrameAt(current, out var resize);
        if (note is null) return false;
        draggedTextNote = note;
        resizingTextFrame = resize;
        textFrameDragStart = CurrentToReference(current, note);
        textFrameOriginalBounds = note.ReferenceBounds;
        selectedTextNote = note;
        SetHoveredTextNote(note);
        Viewer.CaptureMouse();
        e.Handled = true;
        UpdateTextSelectionMenu();
        return true;
    }

    private bool UpdateTextFrameDrag(MouseEventArgs e)
    {
        var note = draggedTextNote;
        if (note is null) return false;
        if (e.LeftButton != MouseButtonState.Pressed) return true;
        var point = CurrentToReference(e.GetPosition(NotesCanvas), note);
        var dx = point.X - textFrameDragStart.X;
        var dy = point.Y - textFrameDragStart.Y;
        var original = textFrameOriginalBounds;
        var limitWidth = note.ReferenceRotation is 90 or 270 ? imageHeight : imageWidth;
        var limitHeight = note.ReferenceRotation is 90 or 270 ? imageWidth : imageHeight;
        Rect bounds;
        if (resizingTextFrame)
        {
            var width = Math.Clamp(original.Width + dx, 1, limitWidth - original.X);
            var height = Math.Clamp(original.Height + dy, 1, limitHeight - original.Y);
            bounds = new Rect(original.X, original.Y, width, height);
        }
        else
        {
            var x = Math.Clamp(original.X + dx, 0, limitWidth - original.Width);
            var y = Math.Clamp(original.Y + dy, 0, limitHeight - original.Height);
            bounds = new Rect(x, y, original.Width, original.Height);
        }
        if (bounds != note.ReferenceBounds)
        {
            if (resizingTextFrame)
            {
                var maximum = MaximumTextFontPixels(note, bounds);
                if (maximum < 1) { e.Handled = true; return true; }
                note.FontPixels = Math.Min(note.FontPixels, maximum);
            }
            note.ReferenceBounds = bounds;
            try
            {
                if (resizingTextFrame) ApplyTextNoteLayout(note);
                PositionTextNote(note);
            }
            catch (Exception ex)
            {
                draggedTextNote = null;
                Viewer.ReleaseMouseCapture();
                ShowNotesError(new StageException("TEXT0007",
                    LanguageManager.Get("TextRenderingFailed"), ex)); // TEXT0007
                e.Handled = true;
                return true;
            }
            notesVersion++;
            noteStatusResult = false;
        }
        e.Handled = true;
        return true;
    }

    private bool FinishTextFrameDrag(MouseButtonEventArgs e)
    {
        if (draggedTextNote is null || e.ChangedButton != MouseButton.Left) return false;
        var note = draggedTextNote;
        draggedTextNote = null;
        Viewer.ReleaseMouseCapture();
        note.ShadowPreviewDelay?.Stop();
        note.ShadowPreviewDelay?.Start();
        UpdateTextFrameHover(e.GetPosition(NotesCanvas));
        RefreshMenu();
        UpdateNoteStatus();
        e.Handled = true;
        return true;
    }

    private void PositionTextNote(TextNote note)
    {
        var bounds = note.ReferenceBounds;
        var p0 = ReferenceToCurrent(new Point(bounds.X, bounds.Y), note.ReferenceRotation);
        var px = ReferenceToCurrent(new Point(bounds.Right, bounds.Y), note.ReferenceRotation);
        var py = ReferenceToCurrent(new Point(bounds.X, bounds.Bottom), note.ReferenceRotation);
        note.Editor.Width = bounds.Width * note.LayoutScale;
        note.Editor.Height = bounds.Height * note.LayoutScale;
        Canvas.SetLeft(note.Editor, p0.X);
        Canvas.SetTop(note.Editor, p0.Y);
        note.Editor.RenderTransform = new MatrixTransform(new Matrix(
            (px.X - p0.X) / (bounds.Width * note.LayoutScale),
            (px.Y - p0.Y) / (bounds.Width * note.LayoutScale),
            (py.X - p0.X) / (bounds.Height * note.LayoutScale),
            (py.Y - p0.Y) / (bounds.Height * note.LayoutScale), 0, 0));
        if (note.SelectionShadowPreview is not null)
        {
            Canvas.SetLeft(note.SelectionShadowPreview, p0.X);
            Canvas.SetTop(note.SelectionShadowPreview, p0.Y);
            note.SelectionShadowPreview.Width = bounds.Width * note.LayoutScale;
            note.SelectionShadowPreview.Height = bounds.Height * note.LayoutScale;
            note.SelectionShadowPreview.RenderTransform = note.Editor.RenderTransform.Clone();
        }
        PositionTextNoteFrame(note);
    }

    private Point ReferenceToCurrent(Point point, int referenceRotation)
    {
        var sourcePoint = ToSourcePoint(point, referenceRotation, imageWidth, imageHeight);
        return FromSourcePoint(sourcePoint, rotation, imageWidth, imageHeight);
    }

    private static Point ToSourcePoint(Point point, int angle, int width, int height) => angle switch
    {
        90 => new Point(point.Y, height - point.X),
        180 => new Point(width - point.X, height - point.Y),
        270 => new Point(width - point.Y, point.X),
        _ => point
    };

    private static Point FromSourcePoint(Point point, int angle, int width, int height) => angle switch
    {
        90 => new Point(height - point.Y, point.X),
        180 => new Point(width - point.X, height - point.Y),
        270 => new Point(point.Y, width - point.X),
        _ => point
    };

    private TextNoteSnapshot SnapshotTextNote(TextNote note)
    {
        var runs = new List<TextRunSnapshot>();
        foreach (var block in note.Editor.Document.Blocks)
        {
            if (block is not Paragraph paragraph) continue;
            foreach (var inline in paragraph.Inlines) CollectRuns(inline, note, runs);
            if (!ReferenceEquals(block, note.Editor.Document.Blocks.LastBlock))
                runs.Add(new TextRunSnapshot("\n", note.Settings.FontFamilyName,
                    note.Settings.Color, note.Settings.Bold, note.Settings.HasShadow,
                    note.Settings.ShadowSize, note.FontPixels));
        }
        return new TextNoteSnapshot(note.ReferenceBounds, note.ReferenceRotation,
            imageWidth, imageHeight,
            note.Settings with { HeightFraction = note.FontPixels / Math.Max(1, note.ReferenceBounds.Height) },
            runs);
    }

    /// <summary>
    /// Freezes editable WPF objects into immutable text and style records. The returned
    /// source owns its own STA renderer and can be consumed from the export worker.
    /// </summary>
    private TextAnnotationSource? CreateTextAnnotationSource(Action<int>? reportProgress = null)
    {
        if (textNotes.Count == 0) return null;
        try { return new TextAnnotationSource(textNotes.Select(SnapshotTextNote).ToArray(),
            reportProgress); }
        catch (Exception ex)
        {
            throw new StageException("TEXT0004", LanguageManager.Get("TextRenderingFailed"), ex); // TEXT0004
        }
    }

    private void CollectRuns(Inline inline, TextNote note,
        List<TextRunSnapshot> result)
    {
        var defaultStyle = note.Settings;
        if (inline is Run run)
        {
            if (run.Text.Length == 0) return;
            var color = (run.Foreground as SolidColorBrush)?.Color ?? defaultStyle.Color;
            var attachedSize = (double)run.GetValue(RunShadowSizeProperty);
            var shadow = double.IsNaN(attachedSize)
                ? (defaultStyle.HasShadow, defaultStyle.ShadowSize)
                : (attachedSize > 0, attachedSize);
            result.Add(new TextRunSnapshot(run.Text, run.FontFamily.Source,
                color, run.FontWeight == FontWeights.Bold, shadow.Item1, shadow.Item2,
                run.FontSize / Math.Max(.001, note.LayoutScale)));
        }
        else if (inline is LineBreak)
            result.Add(new TextRunSnapshot("\n", defaultStyle.FontFamilyName,
                defaultStyle.Color, defaultStyle.Bold, defaultStyle.HasShadow,
                defaultStyle.ShadowSize, note.FontPixels));
        else if (inline is Span span)
            foreach (var child in span.Inlines) CollectRuns(child, note, result);
    }
}
