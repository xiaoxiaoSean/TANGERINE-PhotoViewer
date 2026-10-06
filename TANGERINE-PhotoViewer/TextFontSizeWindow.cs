using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TANGERINE_PhotoViewer;

/// <summary>
/// Edits the source-image font size of one finished text rectangle. The current
/// rectangle supplies its maximum, and the slider cannot leave that range.
/// </summary>
internal sealed class TextFontSizeWindow : Window
{
    internal double SelectedFontPixels { get; private set; }

    internal TextFontSizeWindow(double current, double maximum)
    {
        SelectedFontPixels = current;
        Title = LanguageManager.Get("TextFontSize");
        Background = Brushes.Black;
        Foreground = Brushes.White;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = SystemParameters.WorkArea.Width * .32;
        Height = SystemParameters.WorkArea.Height * .24;
        var root = new Grid { Margin = new Thickness(SystemParameters.WorkArea.Width * .006) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Content = root;
        var group = new Grid();
        group.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        group.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(group);
        var value = new TextBlock { Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center };
        group.Children.Add(value);
        var slider = new Slider { Minimum = 1, Maximum = Math.Max(1, maximum),
            Value = Math.Clamp(current, 1, Math.Max(1, maximum)),
            IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(slider, 1);
        group.Children.Add(slider);
        void UpdateLabel() => value.Text = string.Format(LanguageManager.Get("TextFontSizeRange"),
            slider.Value.ToString("0.#"), slider.Maximum.ToString("0.#"));
        slider.ValueChanged += (_, _) => UpdateLabel();
        UpdateLabel();
        var commands = new Grid();
        commands.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        commands.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(commands, 1);
        root.Children.Add(commands);
        var done = new Button { Content = LanguageManager.Get("TextDone") };
        done.Click += (_, _) => { SelectedFontPixels = slider.Value; DialogResult = true; };
        commands.Children.Add(done);
        var cancel = new Button { Content = LanguageManager.Get("Cancel") };
        cancel.Click += (_, _) => DialogResult = false;
        Grid.SetColumn(cancel, 1);
        commands.Children.Add(cancel);
    }
}
