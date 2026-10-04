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
    private CancellationTokenSource? operation;
    private long generation;
    private bool stopRequested;
    private bool renderSuspended;
    private GifWindow? gifWindow;
    private string? path;
    private BitmapSource? source;
    private bool isGif;
    private bool large;
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
        GifItem.Header = LanguageManager.Get("GifTools");
        DetailsItem.Header = LanguageManager.Get("ImageDetails");
        UnloadItem.Header = LanguageManager.Get("Unload");
        StopItem.Header = LanguageManager.Get("Stop");
        SystemItem.Header = LanguageManager.Get("SystemOperations");
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
        StopItem.Visibility = operation is not null || directoryScan is not null || gifWindow?.IsWorking == true
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateNavigationButtons();
    }

    private CancellationToken BeginWork()
    {
        operation?.Cancel();
        operation = new CancellationTokenSource();
        stopRequested = false;
        generation++;

        RefreshMenu();
        return operation.Token;
    }

    private void EndWork(CancellationToken token)
    {
        if (operation?.Token != token) return;
        operation.Dispose();
        operation = null;

        RefreshMenu();
    }

    private void ShowProgress(string key)
    {

        StatusLabel.Content = string.Format(LanguageManager.Get(key), 0);
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = LanguageManager.Get("FileFilter"), CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) await OpenAsync(dialog.FileName);
    }

    private async Task OpenAsync(string file)
    {
        renderDelay.Stop();
        CancelDirectoryScan();
        renderSuspended = true;
        var token = BeginWork();
        var workGeneration = generation;
        var previous = (path, source, isGif, large, imageWidth, imageHeight, fileSize, imageFormat, scale, rotation, Photo.Source, Preview.Source, Photo.Width, Photo.Height, Surface.Width, Surface.Height);
        try
        {
            ShowProgress("OpeningProgress");
            var progress = new Progress<int>(value => { if (workGeneration == generation && !token.IsCancellationRequested) StatusLabel.Content = string.Format(LanguageManager.Get("OpeningProgress"), value); });
            var result = await Task.Run(() => NativeWork.Run(() => ImageLoader.Load(file, LargeFileBytes, token, progress), token), token);
            token.ThrowIfCancellationRequested();
            if (workGeneration != generation) return;
            path = file;
            source = result.Image;
            isGif = result.IsGif;
            large = result.IsLarge;
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
            StatusLabel.Content = string.Format(LanguageManager.Get("Opened"), System.IO.Path.GetFileName(file));
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
            EndWork(token);
        }
    }

    private void Restore((string? Path, BitmapSource? Source, bool Gif, bool Large, int Width, int Height,
        long FileSize, string Format, double Scale, int Rotation, System.Windows.Media.ImageSource? PhotoSource, System.Windows.Media.ImageSource? PreviewSource,
        double PhotoWidth, double PhotoHeight, double SurfaceWidth, double SurfaceHeight) old)
    {
        (path, source, isGif, large, imageWidth, imageHeight, fileSize, imageFormat, scale, rotation) =
            (old.Path, old.Source, old.Gif, old.Large, old.Width, old.Height, old.FileSize, old.Format, old.Scale, old.Rotation);
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
        var token = BeginWork();
        var workGeneration = generation;
        try
        {
            ShowProgress("UnloadingProgress");
            await Task.Run(() => { token.ThrowIfCancellationRequested(); }, token);
            token.ThrowIfCancellationRequested();
            if (workGeneration != generation) return;
            renderDelay.Stop();
            path = null; source = null; isGif = false; large = false; rotation = 0; scale = 1;
            CancelDirectoryScan();
            neighboringImages = [];
            fileSize = 0; imageFormat = string.Empty;
            Photo.Source = null; Preview.Source = null; Surface.Width = 0; Surface.Height = 0;
            NavigatorImage.Source = null;
            CloseImageWindows();
            StatusLabel.Content = LanguageManager.Get("Ready");
            UpdateZoomLabel();
            RefreshMenu();
        }
        catch (OperationCanceledException) { if (workGeneration == generation) StatusLabel.Content = LanguageManager.Get("Stopped"); }
        finally { EndWork(token); }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        stopRequested = true;
        operation?.Cancel();
        CancelDirectoryScan();
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
            if (large) QueueRender();
        }
        else UpdateZoomControls();
    }

    private void Rotate(int degrees)
    {
        if (source is null) return;
        Photo.Source = large ? null : source;
        rotation = (rotation + degrees + 360) % 360;
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
        if (e.ChangedButton != MouseButton.Left) return;
        dragPoint = e.GetPosition(Viewer);
        Viewer.CaptureMouse();
    }
    private void Viewer_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (dragPoint is not { } old || e.LeftButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(Viewer);
        Viewer.ScrollToHorizontalOffset(Viewer.HorizontalOffset + old.X - current.X);
        Viewer.ScrollToVerticalOffset(Viewer.VerticalOffset + old.Y - current.Y);
        dragPoint = current;
    }
    private void Viewer_PreviewMouseUp(object sender, MouseButtonEventArgs e) { dragPoint = null; Viewer.ReleaseMouseCapture(); }
    private void Viewer_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        UpdateNavigatorViewport();
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
        ZoomSlider.ToolTip = string.Format(LanguageManager.Get("ZoomPercent"), (int)Math.Round(ScaleFromSlider(x) * 100));
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

    private void PreviewArea_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateNavigationButtons();

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

    private void Window_KeyUp(object sender, KeyEventArgs e) => UpdateZoomLabel();
    private void Window_Deactivated(object? sender, EventArgs e) => ZoomLabel.Visibility = Visibility.Collapsed;

    private void QueueRender() { renderDelay.Stop(); renderDelay.Start(); }

    private async Task RenderAsync()
    {
        if (!large || path is null || source is null) return;
        var file = path;
        var token = BeginWork();
        var workGeneration = generation;
        try
        {
            ShowProgress("ReadingProgress");
            var viewX = Math.Max(0, (int)(Viewer.HorizontalOffset / scale));
            var viewY = Math.Max(0, (int)(Viewer.VerticalOffset / scale));
            var viewWidth = Math.Min(rotation is 90 or 270 ? imageHeight : imageWidth, Math.Max(1, (int)(Viewer.ViewportWidth / scale) + 2));
            var viewHeight = Math.Min(rotation is 90 or 270 ? imageWidth : imageHeight, Math.Max(1, (int)(Viewer.ViewportHeight / scale) + 2));
            var viewport = new Int32Rect(viewX, viewY, viewWidth, viewHeight);
            var angle = rotation;
            var displayScale = scale;
            var progress = new Progress<int>(value => { if (workGeneration == generation && !token.IsCancellationRequested) StatusLabel.Content = string.Format(LanguageManager.Get("ReadingProgress"), value); });
            var tile = await Task.Run(() => NativeWork.Run(() => ImageLoader.LoadRegion(file, viewport, angle, displayScale, token, progress), token), token);
            token.ThrowIfCancellationRequested();
            if (workGeneration != generation || file != path) return;
            if (stopRequested) return;
            renderSuspended = true;
            try
            {
                Photo.Source = tile;
                Photo.Width = viewport.Width * scale; Photo.Height = viewport.Height * scale;
                Canvas.SetLeft(Photo, viewport.X * scale); Canvas.SetTop(Photo, viewport.Y * scale);
            }
            finally { renderSuspended = false; }
            StatusLabel.Content = string.Format(LanguageManager.Get("Opened"), System.IO.Path.GetFileName(file));
        }
        catch (OperationCanceledException) { if (workGeneration == generation) StatusLabel.Content = LanguageManager.Get("Stopped"); }
        catch (Exception ex) { if (workGeneration == generation) StatusLabel.Content = string.Format(LanguageManager.Get("OperationFailed"), ex.Message); }
        finally { EndWork(token); }
    }

    private void Gif_Click(object sender, RoutedEventArgs e)
    {
        if (path is null) return;
        if (gifWindow is not null) { gifWindow.Activate(); return; }
        var window = new GifWindow(path) { Owner = this };
        gifWindow = window;
        window.WorkStateChanged += (_, _) => { if (ReferenceEquals(gifWindow, window)) RefreshMenu(); };
        window.ProgressChanged += (_, value) =>
        {
            if (ReferenceEquals(gifWindow, window))
                StatusLabel.Content = string.Format(LanguageManager.Get("ExportProgress"), value);
        };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(gifWindow, window)) gifWindow = null;
            RefreshMenu();
        };
        window.Show();
    }
    private void System_Click(object sender, RoutedEventArgs e) => new SystemWindow { Owner = this }.Show();
}
