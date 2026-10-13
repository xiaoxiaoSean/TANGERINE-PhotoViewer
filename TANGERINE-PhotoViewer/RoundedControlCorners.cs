using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TANGERINE_PhotoViewer;

/// <summary>
/// Rounds control surfaces according to the physical resolution and display
/// scaling of the monitor holding each WPF window or popup. The radius is 8 DIPs
/// on a 1920 x 1080 monitor and changes with the square root of pixel area.
/// WPF's PerMonitorV2 DPI transform then scales that DIP radius to physical pixels.
/// Template elements often have local zero-radius values, so loaded class handlers
/// must set the radius after WPF creates each element.
/// </summary>
internal static class RoundedControlCorners
{
    private const double ReferenceWidth = 1920;
    private const double ReferenceHeight = 1080;
    private const double ReferenceRadius = 8;
    private static readonly ConditionalWeakTable<HwndSource, SourceCorners> Sources = new();

    internal static void Register()
    {
        EventManager.RegisterClassHandler(typeof(Border), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnBorderLoaded), true);
        EventManager.RegisterClassHandler(typeof(Rectangle), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnRectangleLoaded), true);
    }

    private static void OnBorderLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border border) return;
        Apply(border, GetRadius(border));
    }

    private static void OnRectangleLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Rectangle rectangle) return;
        var radius = GetRadius(rectangle);
        rectangle.RadiusX = radius;
        rectangle.RadiusY = radius;
    }

    private static double GetRadius(Visual element)
    {
        var source = PresentationSource.FromVisual(element) as HwndSource;
        return source is null ? ReferenceRadius : Sources.GetValue(source, static key => new SourceCorners(key)).Radius;
    }

    private static void Apply(Border border, double radius)
    {
        // Image surfaces and their navigator frames keep their original shape.
        if (border.Name is "Navigator" or "NavigatorViewport" or "CropPillBorder" ||
            border.Background is ImageBrush || border.Child is Image) return;
        border.CornerRadius = new CornerRadius(radius);
    }

    private static void ApplyToTree(DependencyObject element, double radius)
    {
        if (element is Border border) Apply(border, radius);
        else if (element is Rectangle rectangle)
        {
            rectangle.RadiusX = radius;
            rectangle.RadiusY = radius;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            ApplyToTree(VisualTreeHelper.GetChild(element, index), radius);
    }

    private static double CalculateRadius(int width, int height)
    {
        // A geometric scale based on pixel area treats portrait and ultrawide
        // displays consistently. WPF multiplies this DIP value by the current
        // monitor's DPI scale; multiplying here would apply DPI twice.
        return ReferenceRadius * Math.Sqrt((double)width * height / (ReferenceWidth * ReferenceHeight));
    }

    private sealed class SourceCorners
    {
        private const int WmWindowPosChanged = 0x0047;
        private const int WmDisplayChange = 0x007E;
        private const int WmDpiChanged = 0x02E0;
        private readonly HwndSource _source;
        private bool _refreshQueued;

        internal double Radius { get; private set; } = ReferenceRadius;

        internal SourceCorners(HwndSource source)
        {
            _source = source;
            UpdateRadius();
            _source.AddHook(OnWindowMessage);
        }

        private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message is WmWindowPosChanged or WmDisplayChange or WmDpiChanged && !_refreshQueued)
            {
                _refreshQueued = true;
                // Wait until WPF processes the window position and DPI messages.
                // Never mark the native message handled: WPF owns the window layout.
                _source.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                {
                    _refreshQueued = false;
                    UpdateRadius();
                });
            }
            return IntPtr.Zero;
        }

        private void UpdateRadius()
        {
            if (!DisplayPixelMetrics.TryGetMonitorResolution(_source.Handle, out var width, out var height))
                return;

            var radius = CalculateRadius(width, height);
            if (Math.Abs(radius - Radius) < 0.0001) return;
            Radius = radius;
            if (_source.RootVisual is DependencyObject root) ApplyToTree(root, radius);
        }
    }
}
