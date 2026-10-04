using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TANGERINE_PhotoViewer;

public sealed class DetailsWindow : Window
{
    public DetailsWindow(string path, int width, int height, long fileSize, string format)
    {
        Title = LanguageManager.Get("ImageDetails");
        Icon = IconAssets.WindowIcon;
        Width = 520;
        Height = 290;
        MinWidth = 380;
        Background = Brushes.Black;
        Foreground = Brushes.White;

        var panel = new StackPanel { Margin = new Thickness(16) };
        AddDetail(panel, "DetailsName", Path.GetFileName(path));
        AddDetail(panel, "DetailsPath", path);
        AddDetail(panel, "DetailsResolution", string.Format(CultureInfo.CurrentCulture,
            LanguageManager.Get("DetailsResolutionValue"), width, height));
        AddDetail(panel, "DetailsFileSize", string.Format(CultureInfo.CurrentCulture,
            LanguageManager.Get("DetailsFileSizeValue"), fileSize, fileSize / 1024d / 1024d));
        AddDetail(panel, "DetailsFormat", format);
        Content = new ScrollViewer { Content = panel, Background = Brushes.Black };
    }

    private static void AddDetail(Panel panel, string labelKey, string value)
    {
        panel.Children.Add(new TextBlock
        {
            Text = string.Format(LanguageManager.Get("DetailsRow"), LanguageManager.Get(labelKey), value),
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
    }
}
