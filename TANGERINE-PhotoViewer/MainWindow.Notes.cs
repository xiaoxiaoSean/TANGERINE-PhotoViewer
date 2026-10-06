using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TANGERINE_PhotoViewer.DefaultApps;

namespace TANGERINE_PhotoViewer;

public partial class MainWindow
{
    private enum NoteTool { Brush, Eraser, Text }

    private const int NoteTileSize = 256;

    /// <summary>
    /// Each tile owns exactly 256 × 256 original-image pixels, independent of the preview
    /// resolution. The UI bitmap exists only for that occupied tile; a huge photo with a few
    /// marks therefore allocates memory for those marks rather than for the entire photo.
    /// </summary>
    private sealed class NoteTile
    {
        internal readonly int Width;
        internal readonly int Height;
        internal readonly byte[] Pixels;
        internal readonly WriteableBitmap Bitmap;
        internal readonly Image View = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
        internal int Occupied;

        internal NoteTile(int width, int height)
        {
            Width = width;
            Height = height;
            Pixels = new byte[width * height * 4];
            Bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            RenderOptions.SetBitmapScalingMode(View, BitmapScalingMode.NearestNeighbor);
        }
    }

    private readonly Dictionary<(int X, int Y), NoteTile> noteTiles = new();
    private bool editingNotes;
    private bool drawingNote;
    private bool savingNotes;
    private bool noteStatusResult;
    private Point? lastNotePoint;
    private NoteTool noteTool = NoteTool.Brush;
    private Color brushColor = Colors.White;
    private readonly System.Windows.Threading.DispatcherTimer noteMenuDelay = new()
    {
        Interval = TimeSpan.FromMilliseconds(70)
    };
    private double brushDiameter;
    private double eraserDiameter;
    private bool diameterInitialized;
    private double monitorDpiScale = 1;
    private long notesVersion;
    private long savedNotesVersion;
    private CancellationTokenSource? notesSaveOperation;

    // The version changes on every stroke, including strokes that an eraser
    // later removes completely. No content means an effectively clean image;
    // otherwise the saved version distinguishes later unsaved edits.
    private bool NotesDirty => (noteTiles.Count > 0 || textNotes.Count > 0) &&
        notesVersion != savedNotesVersion;

    private void ApplyNotesText()
    {
        NotesItem.Header = LanguageManager.Get("Notes");
        BrushItem.Header = EditBrushItem.Header = LanguageManager.Get("Brush");
        BrushEraserItem.Header = EditEraserItem.Header = LanguageManager.Get("BrushEraser");
        TextItem.Header = EditTextItem.Header = LanguageManager.Get("TextTool");
        TextSettingsItem.Header = LanguageManager.Get("TextSettings");
        TextFontSizeItem.Header = LanguageManager.Get("TextFontSize");
        SaveAsItem.Header = LanguageManager.Get("SaveAs");
        SwitchToolItem.Header = LanguageManager.Get("SwitchTool");
        ExitEditModeItem.Header = LanguageManager.Get("ExitEditMode");
        ApplyBrushAppearance();
        UpdateNoteStatus();
    }

    private void RefreshNotesMenu(bool loaded)
    {
        if (editingNotes && !loaded) ExitNotesMode();
        foreach (var item in new[] { OpenItem, ZoomInItem, ZoomOutItem, RotateLeftItem, RotateRightItem,
                     NotesItem, SaveAsItem, GifItem, DetailsItem, UnloadItem, StopItem, SystemItem, AboutItem })
            if (editingNotes) item.Visibility = Visibility.Collapsed;
        EditingDocumentItem.Visibility = SwitchToolItem.Visibility = ExitEditModeItem.Visibility =
            editingNotes ? Visibility.Visible : Visibility.Collapsed;
        TextSettingsItem.Visibility = editingNotes && noteTool == NoteTool.Text && HasSelectedText
            ? Visibility.Visible : Visibility.Collapsed;
        TextFontSizeItem.Visibility = editingNotes && noteTool == NoteTool.Text && selectedTextNote is not null
            ? Visibility.Visible : Visibility.Collapsed;
        if (!editingNotes)
        {
            OpenItem.Visibility = SystemItem.Visibility = AboutItem.Visibility = Visibility.Visible;
            foreach (var item in new[] { ZoomInItem, ZoomOutItem, RotateLeftItem, RotateRightItem, DetailsItem, UnloadItem })
                item.Visibility = loaded ? Visibility.Visible : Visibility.Collapsed;
            NotesItem.Visibility = loaded ? Visibility.Visible : Visibility.Collapsed;
            GifItem.Visibility = loaded && isGif ? Visibility.Visible : Visibility.Collapsed;
            SaveAsItem.Visibility = loaded && NotesDirty ? Visibility.Visible : Visibility.Collapsed;
            StopItem.Visibility = operation is not null || directoryScan is not null || gifWindow?.IsWorking == true ||
                cachePreparation is not null || savingNotes ? Visibility.Visible : Visibility.Collapsed;
        }
        EditingDocumentItem.Header = noteTool == NoteTool.Text
            ? LanguageManager.Get("EditingWithTextTool")
            : string.Format(LanguageManager.Get("EditingDocumentWithTool"),
                LanguageManager.Get(noteTool == NoteTool.Brush ? "Brush" : "BrushEraser"));
        EditBrushItem.Background = noteTool == NoteTool.Brush ? Brushes.Gray : Brushes.Black;
        EditEraserItem.Background = noteTool == NoteTool.Eraser ? Brushes.Gray : Brushes.Black;
        EditTextItem.Background = noteTool == NoteTool.Text ? Brushes.Gray : Brushes.Black;
        EditBrushItem.Foreground = EditEraserItem.Foreground = Brushes.White;
        ApplyTextToolAppearance();
        UpdateTextSelectionMenu();
    }

    private void ResetNotesForNewImage()
    {
        noteMenuDelay.Stop();
        noteMenuDelay.Tick -= NotesMenuDelay_Tick;
        ResetTextNotes();
        ExitNotesMode();
        dragPoint = null;
        Viewer.ReleaseMouseCapture();
        NotesCanvas.Children.Clear();
        noteTiles.Clear();
        notesVersion++;
        savedNotesVersion = notesVersion;
        noteStatusResult = false;
        UpdateNoteStatus();
    }

    private void UpdateNotesGeometry()
    {
        if (source is null) return;
        NotesCanvas.Width = rotation is 90 or 270 ? imageHeight : imageWidth;
        NotesCanvas.Height = rotation is 90 or 270 ? imageWidth : imageHeight;
        NotesCanvas.RenderTransform = new ScaleTransform(scale, scale);
        // Image.Source is a raster bitmap tile. Rotations change only its position and
        // RenderTransform; original pixel dimensions and byte arrays stay unchanged.
        foreach (var entry in noteTiles)
            PositionNoteTile(entry.Key.X, entry.Key.Y, entry.Value);
        UpdateTextNoteGeometry();
    }

    private void RotateNotes(int degrees)
    {
        if (noteTiles.Count == 0) return;
        var wasClean = notesVersion == savedNotesVersion;
        notesVersion++;
        if (wasClean) savedNotesVersion = notesVersion;
    }

    private void PositionNoteTile(int tileX, int tileY, NoteTile tile)
    {
        var x = tileX * NoteTileSize;
        var y = tileY * NoteTileSize;
        switch (rotation)
        {
            case 90:
                Canvas.SetLeft(tile.View, imageHeight - y);
                Canvas.SetTop(tile.View, x);
                tile.View.RenderTransform = new RotateTransform(90);
                break;
            case 180:
                Canvas.SetLeft(tile.View, imageWidth - x);
                Canvas.SetTop(tile.View, imageHeight - y);
                tile.View.RenderTransform = new RotateTransform(180);
                break;
            case 270:
                Canvas.SetLeft(tile.View, y);
                Canvas.SetTop(tile.View, imageWidth - x);
                tile.View.RenderTransform = new RotateTransform(270);
                break;
            default:
                Canvas.SetLeft(tile.View, x);
                Canvas.SetTop(tile.View, y);
                tile.View.RenderTransform = Transform.Identity;
                break;
        }
    }

    private void ApplyBrushAppearance()
    {
        var inverse = Color.FromRgb((byte)(255 - brushColor.R), (byte)(255 - brushColor.G),
            (byte)(255 - brushColor.B));
        BrushItem.Foreground = new SolidColorBrush(brushColor);
        BrushItem.Background = new SolidColorBrush(inverse);
    }

    private void EnterNotesMode(NoteTool tool)
    {
        if (source is null || savingNotes) return;
        if (tool != NoteTool.Text)
        {
            try
            {
                EnsureDefaultDiameters();
                monitorDpiScale = DisplayPixelMetrics.Get(this).DpiScale;
            }
            catch (StageException ex) { ShowNotesError(ex); return; }
        }
        noteTool = tool;
        editingNotes = true;
        noteStatusResult = false;
        NotesCanvas.IsHitTestVisible = true;
        UpdateTextEditorsInteractivity();
        if (tool == NoteTool.Text) OnTextModeEntered();
        ApplyBrushAppearance();
        UpdateNoteStatus();
        RefreshMenu();
    }

    private void ExitNotesMode()
    {
        editingNotes = false;
        drawingNote = false;
        lastNotePoint = null;
        CancelTextNoteEditing();
        NotesCanvas.IsHitTestVisible = false;
    }

    private void Brush_Click(object sender, RoutedEventArgs e) => EnterNotesMode(NoteTool.Brush);
    private void BrushEraser_Click(object sender, RoutedEventArgs e) => EnterNotesMode(NoteTool.Eraser);
    private void TextTool_Click(object sender, RoutedEventArgs e) => EnterNotesMode(NoteTool.Text);
    private void TextSettings_Click(object sender, RoutedEventArgs e) => OpenSelectedTextOptions();
    private void TextFontSize_Click(object sender, RoutedEventArgs e) => OpenTextFontSizeOptions();
    private void Text_OptionsRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        NotesItem.IsSubmenuOpen = SwitchToolItem.IsSubmenuOpen = false;
        Dispatcher.BeginInvoke(OpenTextToolOptions, System.Windows.Threading.DispatcherPriority.Input);
    }
    private void ExitEditMode_Click(object sender, RoutedEventArgs e)
    {
        ExitNotesMode();
        UpdateNoteStatus();
        RefreshMenu();
    }

    private void Brush_OptionsRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        NotesItem.IsSubmenuOpen = SwitchToolItem.IsSubmenuOpen = false;
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var (minimum, maximum, dpiScale) = DisplayPixelMetrics.Get(this);
                EnsureDefaultDiameters(minimum);
                var window = new NoteOptionsWindow(true, brushColor, brushDiameter,
                    minimum, maximum, dpiScale) { Owner = this };
                if (window.ShowDialog() != true) return;
                brushColor = window.SelectedColor;
                brushDiameter = window.SelectedDiameter;
                ApplyBrushAppearance();
                UpdateNoteStatus();
            }
            catch (StageException ex)
            {
                ShowNotesError(ex);
            }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Eraser_OptionsRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        NotesItem.IsSubmenuOpen = SwitchToolItem.IsSubmenuOpen = false;
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var (minimum, maximum, dpiScale) = DisplayPixelMetrics.Get(this);
                EnsureDefaultDiameters(minimum);
                var window = new NoteOptionsWindow(false, brushColor, eraserDiameter,
                    minimum, maximum, dpiScale) { Owner = this };
                if (window.ShowDialog() != true) return;
                eraserDiameter = window.SelectedDiameter;
                UpdateNoteStatus();
            }
            catch (StageException ex)
            {
                ShowNotesError(ex);
            }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void EnsureDefaultDiameters(double? initialMinimum = null)
    {
        if (diameterInitialized) return;
        var minimum = initialMinimum ?? DisplayPixelMetrics.Get(this).Minimum;
        brushDiameter = eraserDiameter = minimum;
        diameterInitialized = true;
    }

    private void ShowNotesError(StageException ex)
    {
        noteStatusResult = true;
        EditStatusLabel.Visibility = Visibility.Visible;
        EditStatusLabel.Content = string.Format(LanguageManager.Get("OperationFailed"),
            $"{ex.StageCode}: {ex.Message}");
    }

    private bool HandleNotesMouseDown(MouseButtonEventArgs e)
    {
        if (!editingNotes) return false;
        if (noteTool == NoteTool.Text && TryBeginTextFrameDrag(e)) return true;
        if (e.ChangedButton == MouseButton.Middle ||
            (e.ChangedButton == MouseButton.Left && Keyboard.IsKeyDown(Key.Space)))
        {
            NotesCanvas.IsHitTestVisible = false;
            dragPoint = e.GetPosition(Viewer);
            Viewer.CaptureMouse();
            e.Handled = true;
            return true;
        }
        if (noteTool == NoteTool.Text && IsTextEditorEvent(e)) return false;
        if (e.ChangedButton != MouseButton.Left) return true;
        if (noteTool == NoteTool.Text)
        {
            StartTextNoteDrag(e.GetPosition(NotesCanvas));
            Viewer.CaptureMouse();
            e.Handled = true;
            return true;
        }
        drawingNote = true;
        lastNotePoint = ToOriginalPoint(e.GetPosition(NotesCanvas));
        PaintNoteSegment(lastNotePoint.Value, lastNotePoint.Value);
        Viewer.CaptureMouse();
        e.Handled = true;
        UpdateNoteStatus();
        return true;
    }

    private bool HandleNotesMouseMove(MouseEventArgs e)
    {
        if (UpdateTextFrameDrag(e)) return true;
        UpdateTextFrameHover(e.GetPosition(NotesCanvas));
        if (noteTool == NoteTool.Text && IsTextEditorEvent(e)) return false;
        if (!editingNotes || !drawingNote) return false;
        if (noteTool == NoteTool.Text)
        {
            MoveTextNoteDrag(e.GetPosition(NotesCanvas));
            e.Handled = true;
            return true;
        }
        if (e.LeftButton != MouseButtonState.Pressed || lastNotePoint is null) return true;
        var current = ToOriginalPoint(e.GetPosition(NotesCanvas));
        PaintNoteSegment(lastNotePoint.Value, current);
        lastNotePoint = current;
        e.Handled = true;
        return true;
    }

    private void HandleNotesMouseUp(MouseButtonEventArgs e)
    {
        if (FinishTextFrameDrag(e)) return;
        if (noteTool == NoteTool.Text && IsTextEditorEvent(e)) return;
        if (!editingNotes || e.ChangedButton != MouseButton.Left || dragPoint is not null) return;
        if (noteTool == NoteTool.Text)
        {
            FinishTextNoteDrag(e.GetPosition(NotesCanvas));
            Viewer.ReleaseMouseCapture();
            RefreshMenu();
            UpdateNoteStatus();
            return;
        }
        drawingNote = false;
        lastNotePoint = null;
        Viewer.ReleaseMouseCapture();
        RefreshMenu();
        UpdateNoteStatus();
    }

    private void RestoreNotesHitTestingAfterPan()
    {
        if (editingNotes) NotesCanvas.IsHitTestVisible = true;
    }

    private Point ToOriginalPoint(Point point) => rotation switch
    {
        90 => new Point(point.Y, imageHeight - point.X),
        180 => new Point(imageWidth - point.X, imageHeight - point.Y),
        270 => new Point(imageWidth - point.Y, point.X),
        _ => point
    };

    private void PaintNoteSegment(Point start, Point end)
    {
        if (imageWidth <= 0 || imageHeight <= 0) return;
        if (!double.IsFinite(start.X + start.Y + end.X + end.Y)) return;
        // One displayed image pixel occupies `scale * dpiScale` physical screen pixels.
        // Rasterize the chosen screen diameter into source-image pixels at stroke time.
        var diameter = noteTool == NoteTool.Brush ? brushDiameter : eraserDiameter;
        var radius = diameter / (2 * scale * monitorDpiScale);
        // Clip the center line first; otherwise a pointer move far outside the canvas could
        // create an enormous scan rectangle even though it paints no image pixels.
        var margin = radius + 1;
        if (!ClipNoteSegment(ref start, ref end, -margin, -margin,
                imageWidth + margin, imageHeight + margin)) return;
        var x0 = (int)Math.Clamp(Math.Floor(Math.Min(start.X, end.X) - radius), 0, imageWidth - 1);
        var y0 = (int)Math.Clamp(Math.Floor(Math.Min(start.Y, end.Y) - radius), 0, imageHeight - 1);
        var x1 = (int)Math.Clamp(Math.Ceiling(Math.Max(start.X, end.X) + radius), 0, imageWidth - 1);
        var y1 = (int)Math.Clamp(Math.Ceiling(Math.Max(start.Y, end.Y) + radius), 0, imageHeight - 1);
        if (x0 > x1 || y0 > y1) return;
        var vx = end.X - start.X;
        var vy = end.Y - start.Y;
        var lengthSquared = vx * vx + vy * vy;
        var changed = false;
        var dirtyTiles = new HashSet<(int X, int Y)>();
        // Scan only the stroke's pixel bounds. This is an integer disk/segment rasterizer:
        // each covered original-image pixel is written exactly once into a sparse BGRA tile.
        for (var y = y0; y <= y1; y++)
        {
            // Restrict each scan line to the centerline portion within one radius of it.
            // A diagonal across a large image must not scan its entire bounding rectangle.
            var tMin = 0d;
            var tMax = 1d;
            if (Math.Abs(vy) > 0.0000001)
            {
                var a = (y + .5 - radius - start.Y) / vy;
                var b = (y + .5 + radius - start.Y) / vy;
                tMin = Math.Max(0, Math.Min(a, b));
                tMax = Math.Min(1, Math.Max(a, b));
                if (tMin > tMax) continue;
            }
            var rowLeft = Math.Max(x0, (int)Math.Floor(Math.Min(start.X + tMin * vx,
                start.X + tMax * vx) - radius));
            var rowRight = Math.Min(x1, (int)Math.Ceiling(Math.Max(start.X + tMin * vx,
                start.X + tMax * vx) + radius));
            for (var x = rowLeft; x <= rowRight; x++)
            {
                var t = lengthSquared <= 0 ? 0 : Math.Clamp(((x + .5 - start.X) * vx +
                    (y + .5 - start.Y) * vy) / lengthSquared, 0, 1);
                var dx = x + .5 - (start.X + t * vx);
                var dy = y + .5 - (start.Y + t * vy);
                if (dx * dx + dy * dy > radius * radius) continue;
                var key = (x / NoteTileSize, y / NoteTileSize);
                if (!noteTiles.TryGetValue(key, out var tile))
                {
                    if (noteTool == NoteTool.Eraser) continue;
                    tile = new NoteTile(Math.Min(NoteTileSize, imageWidth - key.Item1 * NoteTileSize),
                        Math.Min(NoteTileSize, imageHeight - key.Item2 * NoteTileSize));
                    tile.View.Source = tile.Bitmap;
                    tile.View.Width = tile.Width;
                    tile.View.Height = tile.Height;
                    noteTiles.Add(key, tile);
                    NotesCanvas.Children.Add(tile.View);
                    PositionNoteTile(key.Item1, key.Item2, tile);
                }
                var pixel = ((y % NoteTileSize) * tile.Width + x % NoteTileSize) * 4;
                var oldAlpha = tile.Pixels[pixel + 3];
                var newAlpha = noteTool == NoteTool.Brush ? (byte)255 : (byte)0;
                if (oldAlpha == newAlpha && (newAlpha == 0 ||
                    (tile.Pixels[pixel] == brushColor.B && tile.Pixels[pixel + 1] == brushColor.G &&
                     tile.Pixels[pixel + 2] == brushColor.R))) continue;
                if (oldAlpha == 0 && newAlpha != 0) tile.Occupied++;
                if (oldAlpha != 0 && newAlpha == 0) tile.Occupied--;
                tile.Pixels[pixel] = newAlpha == 0 ? (byte)0 : brushColor.B;
                tile.Pixels[pixel + 1] = newAlpha == 0 ? (byte)0 : brushColor.G;
                tile.Pixels[pixel + 2] = newAlpha == 0 ? (byte)0 : brushColor.R;
                tile.Pixels[pixel + 3] = newAlpha;
                dirtyTiles.Add(key);
                changed = true;
            }
        }
        if (!changed) return;
        foreach (var key in dirtyTiles)
        {
            var tile = noteTiles[key];
            if (tile.Occupied == 0)
            {
                NotesCanvas.Children.Remove(tile.View);
                noteTiles.Remove(key);
                continue;
            }
            tile.Bitmap.WritePixels(new Int32Rect(0, 0, tile.Width, tile.Height), tile.Pixels,
                tile.Width * 4, 0);
        }
        notesVersion++;
        noteStatusResult = false;
        if (!noteMenuDelay.IsEnabled)
        {
            noteMenuDelay.Tick += NotesMenuDelay_Tick;
            noteMenuDelay.Start();
        }
    }

    private void NotesMenuDelay_Tick(object? sender, EventArgs e)
    {
        noteMenuDelay.Stop();
        noteMenuDelay.Tick -= NotesMenuDelay_Tick;
        RefreshMenu();
    }

    private void UpdateNoteMonitorScale()
    {
        if (!editingNotes) return;
        try
        {
            monitorDpiScale = DisplayPixelMetrics.Get(this).DpiScale;
            UpdateNoteStatus();
        }
        catch (StageException ex) { ShowNotesError(ex); }
    }

    private static bool ClipNoteSegment(ref Point start, ref Point end,
        double left, double top, double right, double bottom)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var enter = 0d;
        var leave = 1d;
        bool Edge(double p, double q)
        {
            if (Math.Abs(p) < 0.0000001) return q >= 0;
            var t = q / p;
            if (p < 0) enter = Math.Max(enter, t);
            else leave = Math.Min(leave, t);
            return enter <= leave;
        }
        if (!Edge(-dx, start.X - left) || !Edge(dx, right - start.X) ||
            !Edge(-dy, start.Y - top) || !Edge(dy, bottom - start.Y)) return false;
        var originalStart = start;
        start = new Point(originalStart.X + enter * dx, originalStart.Y + enter * dy);
        end = new Point(originalStart.X + leave * dx, originalStart.Y + leave * dy);
        return true;
    }

    private IReadOnlyList<AnnotationTile> SnapshotNotes()
    {
        return noteTiles.Select(entry => new AnnotationTile(entry.Key.X * NoteTileSize,
            entry.Key.Y * NoteTileSize, entry.Value.Width, entry.Value.Height,
            (byte[])entry.Value.Pixels.Clone())).ToArray();
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (path is null || !NotesDirty || savingNotes) return;
        var inputPath = path;
        var extension = System.IO.Path.GetExtension(inputPath).TrimStart('.');
        var originalSupported = !string.IsNullOrWhiteSpace(extension) && AnnotationExport.CanSaveAsOriginal(inputPath);
        var filter = originalSupported ? string.Format(LanguageManager.Get("SaveAsOriginalFilter"), extension)
            : LanguageManager.Get("SaveAsPngFilter");
        var dialog = new SaveFileDialog
        {
            Filter = filter,
            DefaultExt = originalSupported ? extension : "png",
            FileName = System.IO.Path.GetFileNameWithoutExtension(inputPath) + "-notes",
            InitialDirectory = System.IO.Path.GetDirectoryName(inputPath),
            FilterIndex = 1,
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        if (string.Equals(System.IO.Path.GetFullPath(dialog.FileName), System.IO.Path.GetFullPath(inputPath),
            StringComparison.OrdinalIgnoreCase))
        {
            EditStatusLabel.Visibility = Visibility.Visible;
            var overwrite = new StageException("NOTES0003", LanguageManager.Get("SaveAsCannotOverwriteOriginal"));
            noteStatusResult = true;
            EditStatusLabel.Content = string.Format(LanguageManager.Get("SaveFailed"),
                $"{overwrite.StageCode}: {overwrite.Message}");
            return;
        }
        var snapshot = SnapshotNotes();
        ClearCompletedTaskProgress();
        var version = notesVersion;
        var angle = rotation;
        var save = new CancellationTokenSource();
        notesSaveOperation = save;
        savingNotes = true;
        noteStatusResult = false;
        EditStatusLabel.Visibility = Visibility.Visible;
        SetTaskProgress("save", LanguageManager.Get("SaveAsAction"), 0);
        RefreshMenu();
        var completed = false;
        var textSourceProgressVisible = false;
        var finalSaveMessage = LanguageManager.Get("Stopped");
        try
        {
            var progress = new Progress<int>(value =>
            {
                if (ReferenceEquals(notesSaveOperation, save))
                    SetTaskProgress("save", LanguageManager.Get("SaveAsAction"), value);
            });
            // Freeze text state on the UI thread, then let the exporter request only
            // the regions it is currently encoding. A large text area cannot force
            // all of its full-resolution raster tiles into managed memory at once.
            using var textSource = CreateTextAnnotationSource(value =>
                Dispatcher.BeginInvoke(() =>
                {
                    if (ReferenceEquals(notesSaveOperation, save))
                        SetTaskProgress("text", LanguageManager.Get("TextRenderingAction"), value);
                }));
            textSourceProgressVisible = textSource is { HasVisibleText: true };
            if (textSourceProgressVisible)
                SetTaskProgress("text", LanguageManager.Get("TextRenderingAction"), 0);
            await AnnotationExport.SaveAsync(inputPath, dialog.FileName, angle,
                snapshot, textSource, save.Token, progress);
            if (ReferenceEquals(notesSaveOperation, save))
            {
                if (version == notesVersion && path == inputPath && angle == rotation)
                    savedNotesVersion = version;
                completed = true;
                noteStatusResult = true;
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(notesSaveOperation, save))
            {
                finalSaveMessage = LanguageManager.Get("Stopped");
                noteStatusResult = true;
            }
        }
        catch (Exception ex)
        {
            var code = ex is StageException stage ? stage.StageCode : "NOTES0004";
            if (ReferenceEquals(notesSaveOperation, save))
            {
                finalSaveMessage = string.Format(LanguageManager.Get("SaveFailed"),
                    $"{code}: {ex.Message}");
                noteStatusResult = true;
            }
        }
        finally
        {
            if (ReferenceEquals(notesSaveOperation, save))
            {
                notesSaveOperation = null;
                savingNotes = false;
                if (completed) ClearCompletedTaskProgress();
                if (completed)
                {
                    if (textSourceProgressVisible)
                        CompleteTaskProgress("text", TaskCompletedMessage("TextRenderingAction"));
                    CompleteTaskProgress("save", string.Format(
                        LanguageManager.Get("SaveAsComplete"), dialog.FileName));
                }
                else
                {
                    ClearTaskProgress("text");
                    // A failed save is a terminal task result too. Keep it in
                    // the shared summary so another running job cannot hide it.
                    CompleteTaskProgress("save", finalSaveMessage);
                }
                RefreshMenu();
            }
            save.Dispose();
        }
    }

    private void UpdateNoteStatus()
    {
        if (EditStatusLabel is null) return;
        if (activeTaskProgress.Count > 0)
        {
            RefreshTaskProgress();
            return;
        }
        EditStatusLabel.Visibility = editingNotes || savingNotes || noteStatusResult
            ? Visibility.Visible : Visibility.Collapsed;
        if (savingNotes || noteStatusResult || !editingNotes) return;
        if (drawingNote)
        {
            EditStatusLabel.Content = LanguageManager.Get(noteTool switch
            {
                NoteTool.Brush => "BrushInUse",
                NoteTool.Eraser => "EraserInUse",
                _ => "TextInUse"
            });
            return;
        }
        if (noteTool == NoteTool.Brush)
        {
            EditStatusLabel.Content = string.Format(LanguageManager.Get("BrushPixelStatus"),
                brushDiameter.ToString("0.##"),
                (brushDiameter / (scale * monitorDpiScale)).ToString("0.##"));
            return;
        }
        EditStatusLabel.Content = noteTool == NoteTool.Text
            ? LanguageManager.Get("TextEditingStatus")
            : LanguageManager.Get(NotesDirty ? "EditedUnsaved" : "EditingUnmodified");
    }

    private void CancelNotesSave() => notesSaveOperation?.Cancel();
}
