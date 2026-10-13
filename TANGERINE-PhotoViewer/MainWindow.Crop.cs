using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using TANGERINE_PhotoViewer.DefaultApps;

namespace TANGERINE_PhotoViewer;

public partial class MainWindow
{
    // Crop coordinates live in the displayed, rotated image's pixel space.
    // Zoom and viewport panning therefore never modify the saved selection.
    private Rect? cropRect;
    private Rect? cropBeforeDrag;
    private Point cropDragStart;
    private bool drawingCrop;
    private bool movingCrop;
    private int activeCropCorner = -1;
    private Path? cropShade;
    private Path? cropOutline;
    private Path? cropHandles;
    private readonly DispatcherTimer cropBoundaryTipDelay = new()
        { Interval = TimeSpan.FromSeconds(2) };
    private readonly Stopwatch cropRecenterClock = new();
    private EventHandler? cropRecenterFrame;

    private void InitializeCropTool()
    {
        cropBoundaryTipDelay.Tick += (_, _) =>
        {
            cropBoundaryTipDelay.Stop();
            CropBoundaryTip.Visibility = Visibility.Collapsed;
        };
    }

    private int OrientedWidth => rotation is 90 or 270 ? imageHeight : imageWidth;
    private int OrientedHeight => rotation is 90 or 270 ? imageWidth : imageHeight;

    private void RotateEditLeft_Click(object sender, RoutedEventArgs e) => RotateForEdit(-90);
    private void RotateEditRight_Click(object sender, RoutedEventArgs e) => RotateForEdit(90);

    private void RotateForEdit(int degrees)
    {
        if (!editingNotes || noteTool != NoteTool.Rotate || source is null || savingNotes) return;
        Rotate(degrees, preserveNoteVersions: true);
        // Rotation is an image edit, even when the picture has no ink or text.
        // Replaying the same inverse operation also restores any crop selection.
        RecordNoteHistory(() => Rotate(-degrees, preserveNoteVersions: true),
            () => Rotate(degrees, preserveNoteVersions: true),
            degrees < 0 ? "TimelineRotateLeftEdit" : "TimelineRotateRightEdit");
        RefreshMenu();
        UpdateNoteStatus();
    }

    private Point CropPixelPoint(MouseEventArgs e)
    {
        var point = e.GetPosition(Surface);
        return new Point(Math.Clamp(point.X / Math.Max(scale, 0.000001), 0, OrientedWidth),
            Math.Clamp(point.Y / Math.Max(scale, 0.000001), 0, OrientedHeight));
    }

    private bool BeginCropDrag(MouseButtonEventArgs e)
    {
        if (source is null || savingNotes) return true;
        var point = CropPixelPoint(e);
        var selected = cropRect ?? new Rect(0, 0, OrientedWidth, OrientedHeight);
        movingCrop = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && selected.Contains(point);
        activeCropCorner = movingCrop ? -1 : HitCropCorner(point, selected);
        // Only a corner can resize the one persistent selection. Ctrl + drag
        // moves it; a click elsewhere must never create a second rectangle.
        if (!movingCrop && activeCropCorner < 0)
        {
            e.Handled = true;
            return true;
        }
        cropBeforeDrag = cropRect;
        cropDragStart = point;
        CancelCropRecenter();
        cropBoundaryTipDelay.Stop();
        CropBoundaryTip.Visibility = Visibility.Collapsed;
        drawingCrop = true;
        Viewer.CaptureMouse();
        e.Handled = true;
        return true;
    }

    private bool UpdateCropDrag(MouseEventArgs e)
    {
        if (!drawingCrop) return false;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            FinishCropDrag(true);
            return true;
        }
        var rawPoint = e.GetPosition(Surface);
        // Keep every corner inside the image. The warning is shown only while
        // a corner is actively pushed beyond an image edge, then cleared as
        // soon as the pointer returns or the drag ends.
        var outsideImage = !movingCrop &&
            (rawPoint.X < 0 || rawPoint.Y < 0 ||
             rawPoint.X > OrientedWidth * scale || rawPoint.Y > OrientedHeight * scale);
        if (outsideImage) ShowCropTip("CropBoundaryTip", true);
        else CropBoundaryTip.Visibility = Visibility.Collapsed;
        var point = CropPixelPoint(e);
        var old = cropBeforeDrag ?? new Rect(0, 0, OrientedWidth, OrientedHeight);
        if (movingCrop)
        {
            var x = Math.Clamp(old.X + point.X - cropDragStart.X, 0, OrientedWidth - old.Width);
            var y = Math.Clamp(old.Y + point.Y - cropDragStart.Y, 0, OrientedHeight - old.Height);
            cropRect = new Rect(x, y, old.Width, old.Height);
        }
        else
        {
            // The opposite corner stays fixed, so edge drags cannot invert the
            // rectangle or generate an invalid zero-area export.
            var left = activeCropCorner is 0 or 2
                ? Math.Clamp(point.X, 0, old.Right - 1) : old.Left;
            var right = activeCropCorner is 1 or 3
                ? Math.Clamp(point.X, old.Left + 1, OrientedWidth) : old.Right;
            var top = activeCropCorner is 0 or 1
                ? Math.Clamp(point.Y, 0, old.Bottom - 1) : old.Top;
            var bottom = activeCropCorner is 2 or 3
                ? Math.Clamp(point.Y, old.Top + 1, OrientedHeight) : old.Bottom;
            cropRect = new Rect(left, top, right - left, bottom - top);
        }
        UpdateCropOverlay();
        e.Handled = true;
        return true;
    }

    private void FinishCropDrag(bool commit)
    {
        if (!drawingCrop) return;
        drawingCrop = false;
        if (commit && CropBoundaryTip.Visibility == Visibility.Visible)
            cropBoundaryTipDelay.Start();
        else CropBoundaryTip.Visibility = Visibility.Collapsed;
        Viewer.ReleaseMouseCapture();
        var before = cropBeforeDrag;
        var after = cropRect;
        cropBeforeDrag = null;
        activeCropCorner = -1;
        if (!commit || after is not { Width: >= 1, Height: >= 1 })
            cropRect = before;
        else
        {
            cropRect = ClampCropToPixels(after.Value);
            if (cropRect == new Rect(0, 0, OrientedWidth, OrientedHeight))
                cropRect = null;
            after = cropRect;
            if (before != after)
                RecordNoteHistory(() => SetCrop(before), () => SetCrop(after),
                    movingCrop ? "TimelineCropMoved" : "TimelineCropped");
        }
        UpdateCropOverlay();
        RefreshMenu();
        UpdateNoteStatus();
    }

    private static Rect ClampCropToPixels(Rect value)
    {
        var left = Math.Floor(value.Left);
        var top = Math.Floor(value.Top);
        return new Rect(left, top, Math.Max(1, Math.Ceiling(value.Right) - left),
            Math.Max(1, Math.Ceiling(value.Bottom) - top));
    }

    private void SetCrop(Rect? value)
    {
        cropRect = value;
        UpdateCropOverlay();
        UpdateNoteStatus();
    }

    private void RotateCrop(int degrees, int oldWidth, int oldHeight)
    {
        if (cropRect is not { } selected) return;
        cropRect = degrees == 90
            ? new Rect(oldHeight - selected.Bottom, selected.Left, selected.Height, selected.Width)
            : new Rect(selected.Top, oldWidth - selected.Right, selected.Height, selected.Width);
        UpdateCropOverlay();
    }

    private void UpdateCropOverlay()
    {
        if (CropCanvas is null) return;
        CropCanvas.Width = Math.Max(0, OrientedWidth * scale);
        CropCanvas.Height = Math.Max(0, OrientedHeight * scale);
        if (cropShade is null)
        {
            cropShade = new Path { Fill = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
                IsHitTestVisible = false };
            cropOutline = new Path { Stroke = Brushes.White, StrokeThickness = 1.5,
                IsHitTestVisible = false };
            cropHandles = new Path { Fill = Brushes.White, Stroke = Brushes.Black,
                StrokeThickness = 1, IsHitTestVisible = false };
            CropCanvas.Children.Add(cropShade);
            CropCanvas.Children.Add(cropOutline);
            CropCanvas.Children.Add(cropHandles);
        }
        if (source is null || (!editingNotes || noteTool != NoteTool.Crop) && cropRect is null)
        {
            cropShade.Data = cropOutline!.Data = cropHandles!.Data = null;
            return;
        }
        var selected = cropRect ?? new Rect(0, 0, OrientedWidth, OrientedHeight);
        var scaled = new Rect(selected.X * scale, selected.Y * scale,
            selected.Width * scale, selected.Height * scale);
        // EvenOdd produces one continuous shaded mask with a rectangular hole.
        // A Path avoids the global rounded-rectangle style changing crop edges.
        cropShade.Data = new GeometryGroup
        {
            FillRule = FillRule.EvenOdd,
            Children = { new RectangleGeometry(new Rect(0, 0, CropCanvas.Width, CropCanvas.Height)),
                new RectangleGeometry(scaled) }
        };
        cropOutline!.Data = new RectangleGeometry(scaled);
        if (editingNotes && noteTool == NoteTool.Crop)
        {
            var radius = CropHandleRadius();
            var handles = new GeometryGroup();
            foreach (var corner in CropCorners(scaled))
                handles.Children.Add(new EllipseGeometry(corner, radius, radius));
            cropHandles!.Data = handles;
        }
        else cropHandles!.Data = null;
    }

    private double CropHandleRadius() => Math.Max(1, Math.Min(Viewer.ViewportWidth,
        Viewer.ViewportHeight) * 0.009);

    private static Point[] CropCorners(Rect selected) =>
    [
        new(selected.Left, selected.Top), new(selected.Right, selected.Top),
        new(selected.Left, selected.Bottom), new(selected.Right, selected.Bottom)
    ];

    private int HitCropCorner(Point point, Rect selected)
    {
        var scaled = new Rect(selected.X * scale, selected.Y * scale,
            selected.Width * scale, selected.Height * scale);
        var pointer = new Point(point.X * scale, point.Y * scale);
        var tolerance = CropHandleRadius() * 2;
        var corners = CropCorners(scaled);
        for (var index = 0; index < corners.Length; index++)
            if ((pointer - corners[index]).Length <= tolerance) return index;
        return -1;
    }

    private void CropRecenter_Click(object sender, RoutedEventArgs e)
    {
        if (source is null) return;
        var selected = cropRect ?? new Rect(0, 0, OrientedWidth, OrientedHeight);
        if (selected.Width <= 0 || selected.Height <= 0) return;
        CancelCropRecenter();
        cropBoundaryTipDelay.Stop();
        CropBoundaryTip.Visibility = Visibility.Collapsed;
        var center = new Point(selected.X + selected.Width / 2,
            selected.Y + selected.Height / 2);
        var startScale = scale;
        var startOffset = new Point(Viewer.HorizontalOffset, Viewer.VerticalOffset);
        var startCenter = Surface.TranslatePoint(new Point(center.X * scale,
            center.Y * scale), Viewer);
        var viewportCenter = new Point(Viewer.ViewportWidth / 2, Viewer.ViewportHeight / 2);
        var fit = Math.Min(Viewer.ViewportWidth / selected.Width,
            Viewer.ViewportHeight / selected.Height);
        var targetScale = Math.Clamp(fit, MinimumScale(), Math.Max(64, MinimumScale()));
        var targetOffset = new Point(center.X * targetScale - viewportCenter.X,
            center.Y * targetScale - viewportCenter.Y);

        // A 260 ms cubic ease accelerates first and decelerates before the
        // selected image region reaches its final size and screen position.
        // RenderTransform supplies the last few pixels of movement when the
        // ScrollViewer clamps an offset at the edge of the full image.
        cropRecenterClock.Restart();
        cropRecenterFrame = (_, _) =>
        {
            var progress = Math.Clamp(cropRecenterClock.Elapsed.TotalMilliseconds / 260, 0, 1);
            var eased = progress < 0.5 ? 4 * progress * progress * progress
                : 1 - Math.Pow(-2 * progress + 2, 3) / 2;
            scale = startScale + (targetScale - startScale) * eased;
            Surface.RenderTransform = Transform.Identity;
            UpdateGeometry();
            Viewer.UpdateLayout();
            Viewer.ScrollToHorizontalOffset(startOffset.X +
                (targetOffset.X - startOffset.X) * eased);
            Viewer.ScrollToVerticalOffset(startOffset.Y +
                (targetOffset.Y - startOffset.Y) * eased);
            Viewer.UpdateLayout();
            var actualCenter = Surface.TranslatePoint(new Point(center.X * scale,
                center.Y * scale), Viewer);
            var desiredCenter = new Point(startCenter.X +
                (viewportCenter.X - startCenter.X) * eased,
                startCenter.Y + (viewportCenter.Y - startCenter.Y) * eased);
            Surface.RenderTransform = new TranslateTransform(desiredCenter.X - actualCenter.X,
                desiredCenter.Y - actualCenter.Y);
            UpdateZoomLabel();
            UpdateZoomControls();
            if (progress < 1) return;
            CancelCropRecenter();
            if (large) QueueRender();
            ShowFooterNotice("CropRecenterCompleted");
        };
        CompositionTarget.Rendering += cropRecenterFrame;
    }

    private void CancelCropRecenter()
    {
        if (cropRecenterFrame is not null)
            CompositionTarget.Rendering -= cropRecenterFrame;
        cropRecenterFrame = null;
        cropRecenterClock.Stop();
    }

    private void ShowCropTip(string key, bool warning)
    {
        CropBoundaryTipText.Text = LanguageManager.Get(key);
        CropBoundaryTip.Background = new SolidColorBrush(warning
            ? Color.FromArgb(221, 65, 22, 22) : Color.FromArgb(221, 0, 0, 0));
        CropBoundaryTip.BorderBrush = warning
            ? new SolidColorBrush(Color.FromRgb(255, 119, 119)) : Brushes.White;
        CropBoundaryTip.Visibility = Visibility.Visible;
    }

    private void CropPillBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        CropPillBorder.CornerRadius = new CornerRadius(e.NewSize.Height / 2);
    }

    private AnnotationCrop? SnapshotCrop()
    {
        if (cropRect is not { } selected) return null;
        var bounded = Rect.Intersect(selected, new Rect(0, 0, OrientedWidth, OrientedHeight));
        if (bounded.IsEmpty || bounded.Width < 1 || bounded.Height < 1) return null;
        return new AnnotationCrop((int)bounded.X, (int)bounded.Y,
            (int)bounded.Width, (int)bounded.Height);
    }
}
