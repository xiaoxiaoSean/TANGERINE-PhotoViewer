using Microsoft.Win32;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Controls.Primitives;
using System.IO;

namespace TANGERINE_PhotoViewer;

public partial class MainWindow : Window
{
    private const long LargeFileBytes = 100L * 1024 * 1024;
    private const double PreviewSamplingFactor = 1.5;
    private const double MaximumPreviewOversamplePixels = 8_000_000;
    private CancellationTokenSource? operation;
    private readonly Dictionary<CancellationToken, CancellationTokenSource> activeOperations = new();
    private long generation;
    private bool stopRequested;
    private bool renderSuspended;
    private GifWindow? gifWindow;
    private string? path;
    private BitmapSource? source;
    private bool isGif;
    private bool large;
    private bool fastRegionAccess;
    private string? preparedRegionPath;
    private CancellationTokenSource? cachePreparation;
    private bool cachePreparationStopped;
    private bool cachePreparationFailed;
    private int imageWidth;
    private int imageHeight;
    private long fileSize;
    private string imageFormat = string.Empty;
    private double scale = 1;
    private int rotation;
    private Point? dragPoint;
    private bool syncingZoomSlider;
    private bool draggingNavigator;
    private double navigatorWidth;
    private double navigatorHeight;
    private bool navigatorSizeChosen;
    private string[] neighboringImages = [];
    private CancellationTokenSource? directoryScan;
    private long directoryScanGeneration;
    private readonly DispatcherTimer renderDelay = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private readonly SemaphoreSlim regionDecodeGate = new(1, 1);
    private readonly DispatcherTimer navigatorLimitDelay = new() { Interval = TimeSpan.FromSeconds(2) };

    private int? displayedZoomPercent;

    public MainWindow()
    {
        InitializeComponent();
        renderDelay.Tick += async (_, _) => { renderDelay.Stop(); await RenderAsync(); };
        navigatorLimitDelay.Tick += (_, _) => { navigatorLimitDelay.Stop(); NavigatorLimitLabel.Visibility = Visibility.Collapsed; };
        ApplyText();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WheelHook);
        if (Environment.GetCommandLineArgs().Skip(1).FirstOrDefault() is { } file)
            _ = OpenAsync(file);
    }

    protected override void OnClosed(EventArgs e)
    {
        renderDelay.Stop();
        CancelNotesSave();
        operation?.Cancel();
        CancelDirectoryScan();
        CancelCachePreparation();
        if (preparedRegionPath is { } cachePath) _ = DeleteCacheWhenIdleAsync(cachePath);
        base.OnClosed(e);
    }

    private IntPtr WheelHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x020E || source is null) return IntPtr.Zero;
        var delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xffff));
        if (Navigator.IsMouseOver && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ResizeNavigatorByWheel(delta);
        }
        else if (Navigator.IsMouseOver || Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            var screenX = unchecked((short)(lParam.ToInt64() & 0xffff));
            var screenY = unchecked((short)((lParam.ToInt64() >> 16) & 0xffff));
            Zoom(delta > 0 ? 1.25 : 0.8, Viewer.PointFromScreen(new Point(screenX, screenY)));
        }
        else Viewer.ScrollToHorizontalOffset(Viewer.HorizontalOffset - delta);
        handled = true;
        return IntPtr.Zero;
    }

    private void ApplyText()
    {
        Title = LanguageManager.Get("AppTitle");
        OpenItem.Header = LanguageManager.Get("Open");
        ZoomInItem.Header = LanguageManager.Get("ZoomIn");
        ZoomOutItem.Header = LanguageManager.Get("ZoomOut");
        RotateLeftItem.Header = LanguageManager.Get("RotateLeft");
        RotateRightItem.Header = LanguageManager.Get("RotateRight");
        ApplyNotesText();
        GifItem.Header = LanguageManager.Get("GifTools");
        DetailsItem.Header = LanguageManager.Get("ImageDetails");
        UnloadItem.Header = LanguageManager.Get("Unload");
        StopItem.Header = LanguageManager.Get("Stop");
        SystemItem.Header = LanguageManager.Get("SystemOperations");
        AboutItem.Header = LanguageManager.Get("About");
        PreviousImageButton.Content = LanguageManager.Get("PreviousImageSymbol");
        NextImageButton.Content = LanguageManager.Get("NextImageSymbol");
        PreviousImageButton.ToolTip = LanguageManager.Get("PreviousImage");
        NextImageButton.ToolTip = LanguageManager.Get("NextImage");
        StatusLabel.Content = LanguageManager.Get("Ready");
        RefreshMenu();
    }

    private void RefreshMenu()
    {
        var loaded = path is not null;
        EmptyLogo.Visibility = loaded ? Visibility.Collapsed : Visibility.Visible;
        ZoomSlider.Visibility = loaded ? Visibility.Visible : Visibility.Collapsed;
        UpdateZoomControls();
        foreach (var item in new[] { ZoomInItem, ZoomOutItem, RotateLeftItem, RotateRightItem, DetailsItem, UnloadItem })
            item.Visibility = loaded ? Visibility.Visible : Visibility.Collapsed;
        GifItem.Visibility = loaded && isGif ? Visibility.Visible : Visibility.Collapsed;
        RefreshNotesMenu(loaded);
        if (!editingNotes)
            StopItem.Visibility = operation is not null || directoryScan is not null || gifWindow?.IsWorking == true
                || cachePreparation is not null || savingNotes
                ? Visibility.Visible : Visibility.Collapsed;
        UpdateNavigationButtons();
    }

    private CancellationToken BeginWork()
    {
        operation?.Cancel();
        // A new image operation supersedes the previous image job. Remove its
        // old progress key now; its canceled callback cannot restore it later.
        ClearActiveImageTaskProgress();
        operation = new CancellationTokenSource();
        activeOperations[operation.Token] = operation;
        stopRequested = false;
        generation++;

        RefreshMenu();
        return operation.Token;
    }

    private void EndWork(CancellationToken token)
    {
        if (operation?.Token == token) operation = null;
        if (activeOperations.Remove(token, out var finished)) finished.Dispose();

        RefreshMenu();
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = LanguageManager.Get("FileFilter"), CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) await OpenAsync(dialog.FileName);
    }

    private async Task OpenAsync(string file)
    {
        ClearCompletedTaskProgress();
        CancelNotesSave();
        renderDelay.Stop();
        CancelDirectoryScan();
        CancelCachePreparation();
        renderSuspended = true;
        var token = BeginWork();
        SetTaskProgress("open", LanguageManager.Get("OpeningAction"), 0);
        var workGeneration = generation;
        var previous = (path, source, isGif, large, fastRegionAccess, imageWidth, imageHeight, fileSize,
            imageFormat, scale, rotation, preparedRegionPath, Photo.Source, Preview.Source,
            Photo.Width, Photo.Height, Surface.Width, Surface.Height);
        var completed = false;
        try
        {
            var progress = new Progress<int>(value =>
            {
                if (workGeneration == generation && operation?.Token == token &&
                    !token.IsCancellationRequested)
                {
                    SetTaskProgress("open", LanguageManager.Get("OpeningAction"), value);
                }
            });
            var (previewWidth, previewHeight) = PreviewPixelSize(Viewer.ActualWidth, Viewer.ActualHeight);
            var result = await Task.Run(() => NativeWork.Run(() => ImageLoader.Load(
                file, LargeFileBytes, previewWidth, previewHeight, token, progress), token), token);
            token.ThrowIfCancellationRequested();
            if (workGeneration != generation) return;
            if (preparedRegionPath is { } oldCache) _ = DeleteCacheWhenIdleAsync(oldCache);
            preparedRegionPath = null;
            cachePreparationStopped = false;
            cachePreparationFailed = false;
            path = file;
            source = result.Image;
            isGif = result.IsGif;
            large = result.IsLarge;
            fastRegionAccess = result.FastRegionAccess;
            imageWidth = result.PixelWidth;
            imageHeight = result.PixelHeight;
            fileSize = result.FileSize;
            imageFormat = result.Format;
            rotation = 0;
            scale = MinimumScale();
            Photo.LayoutTransform = Transform.Identity;
            renderSuspended = true;
            Photo.Source = large ? null : source;
            Preview.Source = large ? source : null;
            UpdateGeometry();
            if (!navigatorSizeChosen) SetDefaultNavigatorSize();
            UpdateNavigatorImage();
            Viewer.ScrollToHorizontalOffset(0);
            Viewer.ScrollToVerticalOffset(0);
            renderSuspended = false;
            UpdateZoomLabel();
            ResetNotesForNewImage();
            StatusLabel.Content = string.Format(LanguageManager.Get("Opened"), System.IO.Path.GetFileName(file));
            completed = true;
            CloseImageWindows();
            _ = ScanDirectoryAsync(file);
            RefreshMenu();
            if (large) QueueRender();
        }
        catch (OperationCanceledException)
        {
            if (workGeneration == generation)
            {
                Restore(previous);
                StatusLabel.Content = LanguageManager.Get("Stopped");
            }
        }
        catch (Exception ex)
        {
            if (workGeneration == generation)
            {
                Restore(previous);
                StatusLabel.Content = string.Format(LanguageManager.Get("OperationFailed"), ex.Message);
            }
        }
        finally
        {
            if (workGeneration == generation) renderSuspended = false;
            if (workGeneration == generation)
            {
                if (completed) CompleteTaskProgress("open",
                    TaskCompletedMessage("OpeningAction"));
                else ClearTaskProgress("open");
            }
            EndWork(token);
        }
    }

    private void Restore((string? Path, BitmapSource? Source, bool Gif, bool Large, bool FastRegionAccess, int Width, int Height,
        long FileSize, string Format, double Scale, int Rotation, string? CachePath,
        System.Windows.Media.ImageSource? PhotoSource, System.Windows.Media.ImageSource? PreviewSource,
        double PhotoWidth, double PhotoHeight, double SurfaceWidth, double SurfaceHeight) old)
    {
        (path, source, isGif, large, fastRegionAccess, imageWidth, imageHeight, fileSize, imageFormat, scale, rotation) =
            (old.Path, old.Source, old.Gif, old.Large, old.FastRegionAccess, old.Width, old.Height,
                old.FileSize, old.Format, old.Scale, old.Rotation);
        preparedRegionPath = old.CachePath;
        Photo.Source = old.PhotoSource;
        Preview.Source = old.PreviewSource;
        Photo.Width = old.PhotoWidth; Photo.Height = old.PhotoHeight;
        Surface.Width = old.SurfaceWidth; Surface.Height = old.SurfaceHeight;
        UpdateGeometry();
        UpdateNavigatorImage();
        RefreshMenu();
    }

    private async void Unload_Click(object sender, RoutedEventArgs e)
    {
        ClearCompletedTaskProgress();
        CancelNotesSave();
        var token = BeginWork();
        var workGeneration = generation;
        var completed = false;
        try
        {
            SetTaskProgress("unload", LanguageManager.Get("Unload"), 0);
            await Task.Run(() => { token.ThrowIfCancellationRequested(); }, token);
            token.ThrowIfCancellationRequested();
            if (workGeneration != generation) return;
            renderDelay.Stop();
            path = null; source = null; isGif = false; large = false; fastRegionAccess = false; rotation = 0; scale = 1;
            ResetNotesForNewImage();
            CancelDirectoryScan();
            CancelCachePreparation();
            if (preparedRegionPath is { } oldCache) _ = DeleteCacheWhenIdleAsync(oldCache);
            preparedRegionPath = null;
            cachePreparationStopped = false;
            cachePreparationFailed = false;
            neighboringImages = [];
            fileSize = 0; imageFormat = string.Empty;
            Photo.Source = null; Preview.Source = null; Surface.Width = 0; Surface.Height = 0;
            NavigatorImage.Source = null;
            CloseImageWindows();
            StatusLabel.Content = LanguageManager.Get("Ready");
            completed = true;
            UpdateZoomLabel();
            RefreshMenu();
        }
        catch (OperationCanceledException) { if (workGeneration == generation) StatusLabel.Content = LanguageManager.Get("Stopped"); }
        finally
        {
            if (workGeneration == generation)
            {
                if (completed) CompleteTaskProgress("unload", TaskCompletedMessage("Unload"));
                else ClearTaskProgress("unload");
            }
            EndWork(token);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        stopRequested = true;
        CancelNotesSave();
        operation?.Cancel();
        CancelDirectoryScan();
        cachePreparationStopped = true;
        CancelCachePreparation();
        gifWindow?.StopWork();

        renderDelay.Stop();
        StatusLabel.Content = LanguageManager.Get("Stopped");
        RefreshMenu();
    }

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedFile(e.Data, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (TryGetDroppedFile(e.Data, out var file)) await OpenAsync(file);
    }

    private static bool TryGetDroppedFile(IDataObject data, out string file)
    {
        file = string.Empty;
        if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] files || files.Length != 1)
            return false;
        file = files[0];
        return System.IO.File.Exists(file);
    }

    private void CloseImageWindows()
    {
        foreach (var window in OwnedWindows.OfType<Window>().ToArray())
        {
            if (window is SystemWindow systemWindow) systemWindow.RequestClose();
            else window.Close();
        }
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if (path is null) return;
        new DetailsWindow(path, imageWidth, imageHeight, fileSize, imageFormat) { Owner = this }.Show();
    }
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Zoom(1.25);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Zoom(0.8);
    private void RotateLeft_Click(object sender, RoutedEventArgs e) => Rotate(-90);
    private void RotateRight_Click(object sender, RoutedEventArgs e) => Rotate(90);

    private void Zoom(double factor, Point? anchor = null)
    {
        if (source is null) return;
        var nextScale = Math.Clamp(scale * factor, MinimumScale(), Math.Max(64, MinimumScale()));
        SetScale(nextScale, anchor);
    }

    private void SetScale(double nextScale, Point? anchor = null)
    {
        if (source is null) return;
        nextScale = Math.Clamp(nextScale, MinimumScale(), Math.Max(64, MinimumScale()));
        var oldScale = scale;
        if (Math.Abs(nextScale - oldScale) < 0.0000001) return;
        var point = anchor ?? new Point(Viewer.ViewportWidth / 2, Viewer.ViewportHeight / 2);
        point.X = Math.Clamp(point.X, 0, Viewer.ViewportWidth);
        point.Y = Math.Clamp(point.Y, 0, Viewer.ViewportHeight);
        var targetX = (Viewer.HorizontalOffset + point.X) * nextScale / oldScale - point.X;
        var targetY = (Viewer.VerticalOffset + point.Y) * nextScale / oldScale - point.Y;
        scale = nextScale;
        UpdateGeometry();
        Viewer.UpdateLayout();
        Viewer.ScrollToHorizontalOffset(targetX);
        Viewer.ScrollToVerticalOffset(targetY);
        UpdateZoomLabel();
        UpdateZoomControls();
        UpdateNoteStatus();
        if (large) QueueRender();
    }

    private void UpdateZoomLabel()
    {
        var show = source is not null && Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        ZoomLabel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        var percent = (int)Math.Round(scale * 100);
        if (displayedZoomPercent == percent) return;
        displayedZoomPercent = percent;
        ZoomLabel.Content = string.Format(LanguageManager.Get("ZoomPercent"), percent);
    }

    private double MinimumScale()
    {
        if (imageWidth <= 0 || imageHeight <= 0) return 1;
        var turned = rotation is 90 or 270;
        var width = turned ? imageHeight : imageWidth;
        var height = turned ? imageWidth : imageHeight;
        var viewportWidth = Math.Max(1, Viewer.ActualWidth - SystemParameters.VerticalScrollBarWidth);
        var viewportHeight = Math.Max(1, Viewer.ActualHeight - SystemParameters.HorizontalScrollBarHeight);
        return Math.Min(1, Math.Min(viewportWidth / width, viewportHeight / height));
    }

    private void Viewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateNavigationButtons();
        if (source is null) return;
        if (!navigatorSizeChosen) SetDefaultNavigatorSize();
        else
        {
            ClampNavigatorArea(false);
            ApplyNavigatorSize();
        }
        var minimum = MinimumScale();
        if (scale < minimum)
        {
            scale = minimum;
            UpdateGeometry();
            UpdateZoomLabel();
            UpdateZoomControls();
        }
        else UpdateZoomControls();
        if (large) QueueRender();
    }

    private void Rotate(int degrees)
    {
        if (source is null) return;
        Photo.Source = large ? null : source;
        rotation = (rotation + degrees + 360) % 360;
        RotateNotes(degrees);
        scale = Math.Max(scale, MinimumScale());
        UpdateGeometry();
        UpdateNavigatorImage();
        UpdateZoomLabel();
        UpdateZoomControls();
        if (large) QueueRender();
    }

    private void UpdateGeometry()
    {
        if (source is null) return;
        var turned = rotation is 90 or 270;
        Surface.Width = (turned ? imageHeight : imageWidth) * scale;
        Surface.Height = (turned ? imageWidth : imageHeight) * scale;
        Preview.Width = Surface.Width; Preview.Height = Surface.Height;
        Canvas.SetLeft(Preview, 0); Canvas.SetTop(Preview, 0);
        if (large && source is not null)
        {
            Preview.Source = rotation switch
            {
                90 => new TransformedBitmap(source, new RotateTransform(90)),
                180 => new TransformedBitmap(source, new RotateTransform(180)),
                270 => new TransformedBitmap(source, new RotateTransform(270)),
                _ => source
            };
        }
        if (!large)
        {
            Photo.Width = imageWidth * scale;
            Photo.Height = imageHeight * scale;
            Photo.LayoutTransform = new RotateTransform(rotation);
        }
        UpdateNotesGeometry();
    }

    private void Viewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Zoom(e.Delta > 0 ? 1.25 : 0.8, e.GetPosition(Viewer));
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) Viewer.ScrollToHorizontalOffset(Viewer.HorizontalOffset - e.Delta);
        else Viewer.ScrollToVerticalOffset(Viewer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private void Viewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Let the text editor receive pointer focus and selection drags. The
        // surrounding viewer must not turn an editor click into image panning.
        if (IsTextEditorEvent(e) && !IsTextFrameHandleEvent(e) &&
            e.ChangedButton != MouseButton.Middle && !Keyboard.IsKeyDown(Key.Space)) return;
        if (HandleNotesMouseDown(e)) return;
        if (e.ChangedButton != MouseButton.Left) return;
        dragPoint = e.GetPosition(Viewer);
        Viewer.CaptureMouse();
    }
    private void Viewer_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (HandleNotesMouseMove(e)) return;
        if (dragPoint is not { } old || (e.LeftButton != MouseButtonState.Pressed && e.MiddleButton != MouseButtonState.Pressed)) return;
        var current = e.GetPosition(Viewer);
        Viewer.ScrollToHorizontalOffset(Viewer.HorizontalOffset + old.X - current.X);
        Viewer.ScrollToVerticalOffset(Viewer.VerticalOffset + old.Y - current.Y);
        dragPoint = current;
    }
    private void Viewer_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        HandleNotesMouseUp(e);
        if (dragPoint is null) return;
        dragPoint = null;
        Viewer.ReleaseMouseCapture();
        RestoreNotesHitTestingAfterPan();
    }
    private void Viewer_MouseLeave(object sender, MouseEventArgs e) => ClearTextFrameHover();
    private void Viewer_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        UpdateNavigatorViewport();
        if (hoveredTextNote is not null) QueueInverseFrameRefresh();
        if (large && !renderSuspended) QueueRender();
    }

    private double ScaleFromSlider(double value)
    {
        var minimum = MinimumScale();
        return minimum * Math.Pow(Math.Max(64, minimum) / minimum, Math.Clamp(value, 0, 1));
    }

    private void UpdateZoomControls()
    {
        if (ZoomSlider is null || Navigator is null) return;
        if (source is null)
        {
            Navigator.Visibility = Visibility.Collapsed;
            return;
        }
        var minimum = MinimumScale();
        syncingZoomSlider = true;
        ZoomSlider.Value = Math.Clamp(Math.Log(scale / minimum) / Math.Log(Math.Max(64, minimum) / minimum), 0, 1);
        syncingZoomSlider = false;
        Navigator.Visibility = scale > minimum * 1.00001 ? Visibility.Visible : Visibility.Collapsed;
        UpdateNavigatorViewport();
        UpdateNavigationButtons();
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!syncingZoomSlider && source is not null) SetScale(ScaleFromSlider(e.NewValue));
    }

    private void ZoomSlider_MouseMove(object sender, MouseEventArgs e)
    {
        if (source is null) return;
        var track = ZoomSlider.Template.FindName("PART_Track", ZoomSlider) as Track;
        var x = track is null
            ? Math.Clamp(e.GetPosition(ZoomSlider).X / Math.Max(1, ZoomSlider.ActualWidth), 0, 1)
            : Math.Clamp(e.GetPosition(track).X / Math.Max(1, track.ActualWidth), 0, 1);
        // Keep the styled ToolTip instance. Replacing it with a string makes WPF
        // create a default system-colored popup instead of the black/white one.
        if (ZoomSlider.ToolTip is ToolTip zoomToolTip)
            zoomToolTip.Content = string.Format(LanguageManager.Get("ZoomPercent"),
                (int)Math.Round(ScaleFromSlider(x) * 100));
    }

    private void SetDefaultNavigatorSize()
    {
        navigatorWidth = Math.Max(1, Viewer.ActualWidth / 8);
        navigatorHeight = Math.Max(1, Viewer.ActualHeight / 7);
        ClampNavigatorArea(false);
        ApplyNavigatorSize();
    }

    private void ClampNavigatorArea(bool showLimit)
    {
        var previewArea = Math.Max(1, Viewer.ActualWidth * Viewer.ActualHeight);
        var currentArea = Math.Max(0.0001, navigatorWidth * navigatorHeight);
        var minimumArea = previewArea / 200;
        var maximumArea = previewArea / 4;
        if (currentArea >= minimumArea && currentArea <= maximumArea) return;

        var tooLarge = currentArea > maximumArea;
        var targetArea = tooLarge ? maximumArea : minimumArea;
        var factor = Math.Sqrt(targetArea / currentArea);
        navigatorWidth *= factor;
        navigatorHeight *= factor;
        if (showLimit) ShowNavigatorLimit(tooLarge ? "NavigatorMaximum" : "NavigatorMinimum");
    }

    private void ShowNavigatorLimit(string key)
    {
        NavigatorLimitLabel.Content = LanguageManager.Get(key);
        NavigatorLimitLabel.Visibility = Visibility.Visible;
        navigatorLimitDelay.Stop();
        navigatorLimitDelay.Start();
    }

    private void ApplyNavigatorSize()
    {
        Navigator.Width = navigatorWidth;
        Navigator.Height = navigatorHeight;
        NavigatorCanvas.Width = Math.Max(1, navigatorWidth - 2);
        NavigatorCanvas.Height = Math.Max(1, navigatorHeight - 2);
        NavigatorResize.Visibility = navigatorWidth < 28 || navigatorHeight < 28 ? Visibility.Collapsed : Visibility.Visible;
        UpdateNavigatorImage();
        UpdateNavigatorViewport();
        UpdateNavigationButtons();
    }

    private void UpdateNavigatorImage()
    {
        if (NavigatorImage is null || source is null) return;
        NavigatorImage.Source = rotation switch
        {
            90 => new TransformedBitmap(source, new RotateTransform(90)),
            180 => new TransformedBitmap(source, new RotateTransform(180)),
            270 => new TransformedBitmap(source, new RotateTransform(270)),
            _ => source
        };
        var displayWidth = rotation is 90 or 270 ? imageHeight : imageWidth;
        var displayHeight = rotation is 90 or 270 ? imageWidth : imageHeight;
        var fit = Math.Min(NavigatorCanvas.Width / displayWidth, NavigatorCanvas.Height / displayHeight);
        NavigatorImage.Width = displayWidth * fit;
        NavigatorImage.Height = displayHeight * fit;
        Canvas.SetLeft(NavigatorImage, (NavigatorCanvas.Width - NavigatorImage.Width) / 2);
        Canvas.SetTop(NavigatorImage, (NavigatorCanvas.Height - NavigatorImage.Height) / 2);
    }

    private void UpdateNavigatorViewport()
    {
        if (NavigatorViewport is null || source is null || NavigatorImage.Width <= 0 || Surface.Width <= 0) return;
        var left = Canvas.GetLeft(NavigatorImage);
        var top = Canvas.GetTop(NavigatorImage);
        NavigatorViewport.Width = Math.Clamp(Viewer.ViewportWidth / Surface.Width * NavigatorImage.Width, 2, NavigatorImage.Width);
        NavigatorViewport.Height = Math.Clamp(Viewer.ViewportHeight / Surface.Height * NavigatorImage.Height, 2, NavigatorImage.Height);
        Canvas.SetLeft(NavigatorViewport, left + Viewer.HorizontalOffset / Surface.Width * NavigatorImage.Width);
        Canvas.SetTop(NavigatorViewport, top + Viewer.VerticalOffset / Surface.Height * NavigatorImage.Height);
    }

    private void NavigatorResize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        navigatorSizeChosen = true;
        navigatorWidth = Math.Max(1, navigatorWidth - e.HorizontalChange);
        navigatorHeight = Math.Max(1, navigatorHeight - e.VerticalChange);
        ClampNavigatorArea(true);
        ApplyNavigatorSize();
    }

    private void Navigator_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) ResizeNavigatorByWheel(e.Delta);
        else Zoom(e.Delta > 0 ? 1.25 : 0.8);
        e.Handled = true;
    }

    private void ResizeNavigatorByWheel(int delta)
    {
        navigatorSizeChosen = true;
        var factor = delta > 0 ? 1.15 : 1 / 1.15;
        navigatorWidth *= factor;
        navigatorHeight *= factor;
        ClampNavigatorArea(true);
        ApplyNavigatorSize();
    }

    private void Navigator_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        draggingNavigator = true;
        NavigatorCanvas.CaptureMouse();
        NavigateTo(e.GetPosition(NavigatorCanvas));
        e.Handled = true;
    }

    private void Navigator_MouseMove(object sender, MouseEventArgs e)
    {
        if (draggingNavigator && e.LeftButton == MouseButtonState.Pressed) NavigateTo(e.GetPosition(NavigatorCanvas));
    }

    private void Navigator_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        draggingNavigator = false;
        NavigatorCanvas.ReleaseMouseCapture();
    }

    private void NavigateTo(Point point)
    {
        if (source is null || NavigatorImage.Width <= 0) return;
        var x = Math.Clamp((point.X - Canvas.GetLeft(NavigatorImage)) / NavigatorImage.Width, 0, 1);
        var y = Math.Clamp((point.Y - Canvas.GetTop(NavigatorImage)) / NavigatorImage.Height, 0, 1);
        Viewer.ScrollToHorizontalOffset(x * Surface.Width - Viewer.ViewportWidth / 2);
        Viewer.ScrollToVerticalOffset(y * Surface.Height - Viewer.ViewportHeight / 2);
    }

    private void PreviewArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateNavigationButtons();
        UpdateNoteMonitorScale();
    }

    private void UpdateNavigationButtons()
    {
        if (PreviousImageButton is null || NextImageButton is null || PreviewArea is null) return;
        var currentIndex = Array.FindIndex(neighboringImages, candidate =>
            string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase));
        var hasOtherImage = neighboringImages.Length > 1;
        var showPrevious = hasOtherImage && path is not null && currentIndex >= 0;
        var showNext = showPrevious;
        PreviousImageButton.Visibility = showPrevious ? Visibility.Visible : Visibility.Collapsed;
        NextImageButton.Visibility = showNext ? Visibility.Visible : Visibility.Collapsed;
        if (!showPrevious && !showNext) return;

        var areaWidth = PreviewArea.ActualWidth;
        var areaHeight = PreviewArea.ActualHeight;
        if (areaWidth <= 0 || areaHeight <= 0) return;
        var buttonWidth = areaWidth / 30;
        var buttonHeight = areaHeight / 7;
        PreviousImageButton.Width = NextImageButton.Width = buttonWidth;
        PreviousImageButton.Height = NextImageButton.Height = buttonHeight;
        Canvas.SetLeft(PreviousImageButton, 0);
        Canvas.SetLeft(NextImageButton, areaWidth - buttonWidth);
        var baseTop = (areaHeight - buttonHeight) / 2;
        Canvas.SetTop(PreviousImageButton, baseTop);
        var nextTop = baseTop;
        var navigatorLeft = areaWidth - Navigator.Margin.Right - Navigator.Width;
        var navigatorRight = areaWidth - Navigator.Margin.Right;
        var navigatorTop = areaHeight - Navigator.Height;
        if (showNext && Navigator.Visibility == Visibility.Visible &&
            areaWidth - buttonWidth < navigatorRight && areaWidth > navigatorLeft &&
            nextTop < areaHeight && nextTop + buttonHeight > navigatorTop)
            nextTop = Math.Max(0, navigatorTop - buttonHeight);
        Canvas.SetTop(NextImageButton, nextTop);
    }

    private void CancelDirectoryScan()
    {
        directoryScanGeneration++;
        directoryScan?.Cancel();
        directoryScan = null;
    }

    private async Task ScanDirectoryAsync(string currentFile)
    {
        CancelDirectoryScan();
        var scan = new CancellationTokenSource();
        directoryScan = scan;
        var scanGeneration = directoryScanGeneration;
        neighboringImages = [currentFile];
        UpdateNavigationButtons();
        try
        {
            var files = await Task.Run(() =>
            {
                var directory = Path.GetDirectoryName(currentFile) ?? throw new DirectoryNotFoundException();
                var found = new List<string>();
                foreach (var candidate in Directory.EnumerateFiles(directory))
                {
                    scan.Token.ThrowIfCancellationRequested();
                    if (string.Equals(candidate, currentFile, StringComparison.OrdinalIgnoreCase) ||
                        ImageLoader.CanDecode(candidate, scan.Token)) found.Add(candidate);
                }
                found.Sort(StringComparer.CurrentCultureIgnoreCase);
                return found.ToArray();
            }, scan.Token);
            if (scanGeneration != directoryScanGeneration || path != currentFile) return;
            neighboringImages = files;
            UpdateNavigationButtons();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (scanGeneration == directoryScanGeneration)
                StatusLabel.Content = string.Format(LanguageManager.Get("OperationFailed"), ex.Message);
        }
        finally
        {
            if (ReferenceEquals(directoryScan, scan)) directoryScan = null;
            scan.Dispose();
            RefreshMenu();
        }
    }

    private async Task NavigateImageAsync(int direction)
    {
        if (path is null) return;
        var index = Array.FindIndex(neighboringImages, candidate =>
            string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || neighboringImages.Length < 2) return;
        var nextIndex = (index + direction + neighboringImages.Length) % neighboringImages.Length;
        await OpenAsync(neighboringImages[nextIndex]);
    }

    private async void PreviousImage_Click(object sender, RoutedEventArgs e) => await NavigateImageAsync(-1);
    private async void NextImage_Click(object sender, RoutedEventArgs e) => await NavigateImageAsync(1);
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // The editable text overlay owns navigation and selection keys while it has focus.
        // Treating those keys as photo navigation would discard the user's editing context.
        if (IsTextEditorFocused()) return;
        UpdateZoomLabel();
        if (Keyboard.Modifiers == ModifierKeys.None &&
            e.Key is Key.Left or Key.Up or Key.Right or Key.Down)
        {
            _ = NavigateImageAsync(e.Key is Key.Left or Key.Up ? -1 : 1);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key is Key.OemPlus or Key.Add) Zoom(1.25);
        if (e.Key is Key.OemMinus or Key.Subtract) Zoom(0.8);
    }

    private void Window_KeyUp(object sender, KeyEventArgs e)
    {
        UpdateZoomLabel();
        if (editingNotes && e.Key == Key.Space && dragPoint is not null)
        {
            dragPoint = null;
            Viewer.ReleaseMouseCapture();
            RestoreNotesHitTestingAfterPan();
        }
    }
    private void Window_Deactivated(object? sender, EventArgs e) => ZoomLabel.Visibility = Visibility.Collapsed;

    private void QueueRender()
    {
        renderDelay.Stop();
        if (!large || source is null) return;
        if (scale <= MinimumScale() * 1.00001)
        {
            operation?.Cancel();
            Photo.Source = null;
            if (PreviewNeedsMorePixels()) renderDelay.Start();
            return;
        }
        if (!fastRegionAccess)
        {
            if (preparedRegionPath is null)
            {
                Photo.Source = null;
                if (path is not null && cachePreparation is null && !cachePreparationStopped && !cachePreparationFailed)
                    _ = PrepareCacheAsync(path);
                return;
            }
        }
        renderDelay.Start();
    }

    private bool PreviewNeedsMorePixels()
    {
        if (source is null) return false;
        var (displayedWidth, displayedHeight) = PreviewPixelSize(Surface.Width, Surface.Height);
        var previewWidth = rotation is 90 or 270 ? source.PixelHeight : source.PixelWidth;
        var previewHeight = rotation is 90 or 270 ? source.PixelWidth : source.PixelHeight;
        return previewWidth + 1 < displayedWidth || previewHeight + 1 < displayedHeight;
    }

    private (int Width, int Height) PreviewPixelSize(double width, double height)
    {
        var dpi = VisualTreeHelper.GetDpi(Viewer);
        var physicalWidth = Math.Max(1, width * dpi.DpiScaleX);
        var physicalHeight = Math.Max(1, height * dpi.DpiScaleY);
        var factor = Math.Min(PreviewSamplingFactor,
            Math.Max(1, Math.Sqrt(MaximumPreviewOversamplePixels / (physicalWidth * physicalHeight))));
        return ((int)Math.Ceiling(Math.Min(int.MaxValue, physicalWidth * factor)),
            (int)Math.Ceiling(Math.Min(int.MaxValue, physicalHeight * factor)));
    }

    private void CancelCachePreparation()
    {
        cachePreparation?.Cancel();
        cachePreparation = null;
        ClearTaskProgress("cache");
        RefreshMenu();
    }

    private async Task DeleteCacheWhenIdleAsync(string cachePath)
    {
        await regionDecodeGate.WaitAsync();
        try
        {
            if (File.Exists(cachePath)) File.Delete(cachePath);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { regionDecodeGate.Release(); }
    }

    private async Task PrepareCacheAsync(string file)
    {
        var work = new CancellationTokenSource();
        cachePreparation = work;
        var cachePath = Path.Combine(Path.GetTempPath(), "TangerinePhotoViewer",
            "ImageCache", Guid.NewGuid().ToString("N") + ".tif");
        RefreshMenu();
        SetTaskProgress("cache", LanguageManager.Get("PreparingCacheAction"), 0);
        var completed = false;
        try
        {
            var progress = new Progress<int>(value =>
            {
                if (ReferenceEquals(cachePreparation, work) && !work.IsCancellationRequested && value > 0)
                {
                    SetTaskProgress("cache", LanguageManager.Get("PreparingCacheAction"), value);
                }
            });
            await Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                ImageLoader.PrepareRandomAccessCache(file, cachePath, work.Token, progress);
            }, work.Token);
            work.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(cachePreparation, work) || path != file) return;
            preparedRegionPath = cachePath;
            StatusLabel.Content = LanguageManager.Get("ImageCacheReady");
            completed = true;
            QueueRender();
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(cachePreparation, work)) StatusLabel.Content = LanguageManager.Get("Stopped");
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(cachePreparation, work))
            {
                cachePreparationFailed = true;
                StatusLabel.Content = string.Format(LanguageManager.Get("OperationFailed"), ex.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(cachePreparation, work))
            {
                if (completed) CompleteTaskProgress("cache",
                    TaskCompletedMessage("PreparingCacheAction"));
                else ClearTaskProgress("cache");
            }
            if (ReferenceEquals(cachePreparation, work)) cachePreparation = null;
            if (cachePath != preparedRegionPath)
                try { if (File.Exists(cachePath)) File.Delete(cachePath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            work.Dispose();
            RefreshMenu();
        }
    }

    private async Task RenderAsync()
    {
        if (!large || path is null || source is null) return;
        if (scale <= MinimumScale() * 1.00001)
        {
            if (PreviewNeedsMorePixels()) await RefreshPreviewAsync();
            return;
        }
        if (!fastRegionAccess && preparedRegionPath is null) return;
        var file = path;
        var decodePath = fastRegionAccess ? file : preparedRegionPath!;
        var token = BeginWork();
        SetTaskProgress("region", LanguageManager.Get("ReadingRegionAction"), 0);
        var workGeneration = generation;
        var completed = false;
        try
        {
            var fullWidth = rotation is 90 or 270 ? imageHeight : imageWidth;
            var fullHeight = rotation is 90 or 270 ? imageWidth : imageHeight;
            var viewX = Math.Clamp((int)(Viewer.HorizontalOffset / scale), 0, fullWidth - 1);
            var viewY = Math.Clamp((int)(Viewer.VerticalOffset / scale), 0, fullHeight - 1);
            var viewWidth = Math.Min(fullWidth - viewX, Math.Max(1, (int)Math.Min(int.MaxValue - 2d,
                Math.Ceiling(Viewer.ViewportWidth / scale)) + 2));
            var viewHeight = Math.Min(fullHeight - viewY, Math.Max(1, (int)Math.Min(int.MaxValue - 2d,
                Math.Ceiling(Viewer.ViewportHeight / scale)) + 2));
            var viewport = new Int32Rect(viewX, viewY, viewWidth, viewHeight);
            var angle = rotation;
            var dpi = VisualTreeHelper.GetDpi(Viewer);
            var displayScale = scale * Math.Max(dpi.DpiScaleX, dpi.DpiScaleY);
            var progress = new Progress<int>(value =>
            {
                if (value > 0 && workGeneration == generation && operation?.Token == token &&
                    !token.IsCancellationRequested)
                {
                    SetTaskProgress("region", LanguageManager.Get("ReadingRegionAction"), value);
                }
            });
            BitmapSource? tile;
            await regionDecodeGate.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                tile = await ImageLoader.LoadRegionParallelAsync(decodePath, viewport, angle, displayScale, token, progress);
            }
            finally { regionDecodeGate.Release(); }
            token.ThrowIfCancellationRequested();
            if (workGeneration != generation || file != path || angle != rotation ||
                (!fastRegionAccess && decodePath != preparedRegionPath) ||
                Math.Abs(displayScale - scale * Math.Max(VisualTreeHelper.GetDpi(Viewer).DpiScaleX,
                    VisualTreeHelper.GetDpi(Viewer).DpiScaleY)) > 0.0000001 ||
                scale <= MinimumScale() * 1.00001) return;
            if (stopRequested) return;
            if (tile is null)
            {
                Photo.Source = null;
                StatusLabel.Content = LanguageManager.Get("LargePreviewOnly");
                completed = true;
                return;
            }
            renderSuspended = true;
            try
            {
                Photo.Source = tile;
                Photo.Width = viewport.Width * scale; Photo.Height = viewport.Height * scale;
                Canvas.SetLeft(Photo, viewport.X * scale); Canvas.SetTop(Photo, viewport.Y * scale);
                if (hoveredTextNote is not null) QueueInverseFrameRefresh();
            }
            finally { renderSuspended = false; }
            StatusLabel.Content = string.Format(LanguageManager.Get("Opened"), System.IO.Path.GetFileName(file));
            completed = true;
        }
        catch (OperationCanceledException) { if (workGeneration == generation) StatusLabel.Content = LanguageManager.Get("Stopped"); }
        catch (Exception ex) { if (workGeneration == generation) StatusLabel.Content = string.Format(LanguageManager.Get("OperationFailed"), ex.Message); }
        finally
        {
            if (workGeneration == generation)
            {
                if (completed) CompleteTaskProgress("region",
                    TaskCompletedMessage("ReadingRegionAction"));
                else ClearTaskProgress("region");
            }
            EndWork(token);
        }
    }

    private async Task RefreshPreviewAsync()
    {
        if (path is null || source is null) return;
        var file = path;
        var angle = rotation;
        var requestedScale = scale;
        var (width, height) = PreviewPixelSize(Surface.Width, Surface.Height);
        if (angle is 90 or 270) (width, height) = (height, width);
        var token = BeginWork();
        SetTaskProgress("preview", LanguageManager.Get("ReadingRegionAction"), 0);
        var workGeneration = generation;
        var completed = false;
        try
        {
            var progress = new Progress<int>(value =>
            {
                if (value > 0 && workGeneration == generation && operation?.Token == token &&
                    !token.IsCancellationRequested)
                {
                    SetTaskProgress("preview", LanguageManager.Get("ReadingRegionAction"), value);
                }
            });
            var preview = await Task.Run(() => ImageLoader.LoadPreview(file, width, height, token, progress), token);
            token.ThrowIfCancellationRequested();
            if (workGeneration != generation || file != path || angle != rotation ||
                Math.Abs(requestedScale - scale) > 0.0000001 ||
                scale > MinimumScale() * 1.00001 || stopRequested) return;
            source = preview;
            UpdateGeometry();
            UpdateNavigatorImage();
            StatusLabel.Content = string.Format(LanguageManager.Get("Opened"), System.IO.Path.GetFileName(file));
            completed = true;
        }
        catch (OperationCanceledException) { if (workGeneration == generation) StatusLabel.Content = LanguageManager.Get("Stopped"); }
        catch (Exception ex) { if (workGeneration == generation) StatusLabel.Content = string.Format(LanguageManager.Get("OperationFailed"), ex.Message); }
        finally
        {
            if (workGeneration == generation)
            {
                if (completed) CompleteTaskProgress("preview",
                    TaskCompletedMessage("ReadingRegionAction"));
                else ClearTaskProgress("preview");
            }
            EndWork(token);
        }
    }

    private void Gif_Click(object sender, RoutedEventArgs e)
    {
        if (path is null) return;
        if (gifWindow is not null) { gifWindow.Activate(); return; }
        var window = new GifWindow(path) { Owner = this };
        gifWindow = window;
        window.WorkStateChanged += (_, _) =>
        {
            if (!ReferenceEquals(gifWindow, window)) return;
            if (!window.IsWorking)
            {
                if (window.LastExportSucceeded)
                    CompleteTaskProgress("gif", TaskCompletedMessage("ExportFrames"));
                else ClearTaskProgress("gif");
            }
            else
            {
                ClearCompletedTaskProgress();
                SetTaskProgress("gif", LanguageManager.Get("ExportFrames"), 0);
            }
            RefreshMenu();
        };
        window.ProgressChanged += (_, value) =>
        {
            if (ReferenceEquals(gifWindow, window))
                SetTaskProgress("gif", LanguageManager.Get("ExportFrames"), value);
        };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(gifWindow, window)) gifWindow = null;
            if (window.IsWorking) ClearTaskProgress("gif");
            RefreshMenu();
        };
        window.Show();
    }
    private void System_Click(object sender, RoutedEventArgs e) => new SystemWindow { Owner = this }.Show();
    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();
}
