using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TANGERINE_PhotoViewer;

public sealed class AboutWindow : Window
{
    public AboutWindow()
    {
        Title = LanguageManager.Get("About");
        Icon = IconAssets.WindowIcon;
        Width = 620;
        Height = 280;
        MinWidth = 480;
        MinHeight = 230;
        Background = Brushes.Black;
        Foreground = Brushes.White;

        var version = typeof(AboutWindow).Assembly.GetName().Version?.ToString(3)
            ?? LanguageManager.Get("UnknownVersion");
        var lines = new[]
        {
            LanguageManager.Get("AboutProduct"),
            LanguageManager.Get("AboutThanks"),
            LanguageManager.Get("AboutFounder"),
            string.Format(LanguageManager.Get("AboutVersion"), version),
            string.Format(LanguageManager.Get("AboutProjectLink"), LanguageManager.Get("AboutProjectUrl"))
        };

        Content = new TextBox
        {
            Text = string.Join(Environment.NewLine, lines),
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Black,
            Foreground = Brushes.White,
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(16),
            Padding = new Thickness(12),
            FontSize = 16
        };
    }
}
