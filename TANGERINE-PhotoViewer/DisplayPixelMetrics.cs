using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using TANGERINE_PhotoViewer.DefaultApps;

namespace TANGERINE_PhotoViewer;

/// <summary>
/// Converts between physical pixels on the monitor containing the viewer and WPF
/// device-independent units. Monitor rectangles from Win32 are physical pixels;
/// WPF's device transform supplies the matching scale for preview geometry.
/// </summary>
internal static class DisplayPixelMetrics
{
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    /// <summary>
    /// Reads the physical pixel resolution of the monitor containing the largest
    /// part of an HWND. The false result lets visual styling retain its baseline
    /// radius when a window is closing or its monitor information is unavailable.
    /// </summary>
    internal static bool TryGetMonitorResolution(IntPtr windowHandle, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (windowHandle == IntPtr.Zero) return false;

        var monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfoW(monitor, ref info)) return false;

        width = info.Monitor.Right - info.Monitor.Left;
        height = info.Monitor.Bottom - info.Monitor.Top;
        return width > 0 && height > 0;
    }

    internal static (double Minimum, double Maximum, double DpiScale) Get(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (!TryGetMonitorResolution(handle, out var width, out var height))
            throw new StageException("NOTES0005", LanguageManager.Get("DisplayMetricsUnavailable"));
        var average = (width + (double)height) / 2;
        var source = PresentationSource.FromVisual(window);
        var dpiScale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        if (!double.IsFinite(dpiScale) || dpiScale <= 0) dpiScale = 1;
        return (average / 400, average / 40, dpiScale);
    }
}
