using System.Windows.Media.Imaging;

namespace TANGERINE_PhotoViewer;

internal static class IconAssets
{
    internal static BitmapImage WindowIcon { get; } = new(new Uri(
        "pack://application:,,,/Assets/TANGERINE-PV.png", UriKind.Absolute));
}
