using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace TANGERINE_PhotoViewer;

/// <summary>
/// Settings shared by newly created text rectangles. Font size is stored as a
/// fraction of the dragged rectangle's image-space height, so screen zoom never
/// changes the text's size in the exported original-resolution image.
/// </summary>
internal sealed record TextToolSettings(string FontFamilyName, double HeightFraction,
    Color Color, bool Bold, bool HasShadow, double ShadowSize)
{
    internal static TextToolSettings Default => new("Segoe UI", .72, Colors.White, false, false, 2);
}

/// <summary>
/// A proportional modal settings window. Selection mode edits font size in
/// source-image pixels and caps it at what the current rectangle can contain.
/// </summary>
internal sealed class TextToolOptionsWindow : Window
{
    private readonly ComboBox fontSelector;
    private readonly Slider sizeSelector;
    private readonly CheckBox boldSelector;
    private readonly CheckBox shadowSelector;
    private readonly Slider shadowSizeSelector;
    private readonly TextBox hexInput;
    private readonly Border colorPreview;
    private Color selectedColor;

    internal TextToolSettings SelectedSettings { get; private set; }
    internal double SelectedFontPixels { get; private set; }

    internal TextToolOptionsWindow(TextToolSettings initial, bool selectedRange,
        double selectedFontPixels = 1, double selectedMaximumPixels = 1)
    {
        SelectedSettings = initial;
        SelectedFontPixels = selectedFontPixels;
        selectedColor = initial.Color;
        Title = LanguageManager.Get(selectedRange ? "TextSettingsTitle" : "TextOptionsTitle");
        Background = Brushes.Black;
        Foreground = Brushes.White;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = SystemParameters.WorkArea.Width * .38;
        Height = SystemParameters.WorkArea.Height * .54;
        MinWidth = SystemParameters.WorkArea.Width * .25;
        MinHeight = SystemParameters.WorkArea.Height * .35;

        var root = new Grid { Margin = new Thickness(SystemParameters.WorkArea.Width * .006) };
        for (var i = 0; i < 6; i++)
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(
                i == 3 ? 2 : 1, GridUnitType.Star) });
        Content = root;

        fontSelector = new ComboBox
        {
            IsEditable = true,
            ItemsSource = Fonts.SystemFontFamilies.OrderBy(font => font.Source).Select(font => font.Source),
            Text = initial.FontFamilyName,
            Background = Brushes.Black,
            Foreground = Brushes.White,
            // The popup and editable TextBox use their own templates, so the
            // ComboBox colors alone cannot keep highlighted text readable.
            ItemContainerStyle = CreateFontItemStyle(),
            VerticalAlignment = VerticalAlignment.Center
        };
        fontSelector.Loaded += (_, _) =>
        {
            // The default editable template can paint its text field white even
            // when the outer ComboBox is black. Set the actual template part.
            if (fontSelector.Template.FindName("PART_EditableTextBox", fontSelector) is not TextBox input)
                return;
            input.Background = Brushes.Black;
            input.Foreground = Brushes.White;
            input.CaretBrush = Brushes.White;
            input.SelectionBrush = new SolidColorBrush(Color.FromRgb(36, 75, 130));
        };
        AddLabeledControl(root, 0, LanguageManager.Get("TextFont"), fontSelector);

        if (selectedRange)
        {
            var maximum = Math.Max(1, selectedMaximumPixels);
            sizeSelector = new Slider
            {
                Minimum = 1,
                Maximum = maximum,
                Value = Math.Clamp(selectedFontPixels, 1, maximum),
                IsMoveToPointEnabled = true,
                VerticalAlignment = VerticalAlignment.Center
            };
            var sizeGroup = new Grid();
            sizeGroup.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            sizeGroup.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var sizeLabel = new TextBlock { Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center };
            sizeGroup.Children.Add(sizeLabel);
            Grid.SetRow(sizeSelector, 1);
            sizeGroup.Children.Add(sizeSelector);
            Grid.SetRow(sizeGroup, 1);
            root.Children.Add(sizeGroup);
            void UpdateSizeLabel() => sizeLabel.Text = string.Format(
                LanguageManager.Get("TextFontSizeRange"),
                sizeSelector.Value.ToString("0.#"), sizeSelector.Maximum.ToString("0.#"));
            sizeSelector.ValueChanged += (_, _) => UpdateSizeLabel();
            UpdateSizeLabel();
        }
        else
        {
            sizeSelector = new Slider
            {
                Minimum = 10,
                Maximum = 100,
                Value = initial.HeightFraction * 100,
                IsMoveToPointEnabled = true,
                VerticalAlignment = VerticalAlignment.Center
            };
            AddLabeledControl(root, 1, LanguageManager.Get("TextFontSizeRatio"), sizeSelector);
        }

        var flags = new Grid();
        flags.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        flags.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        boldSelector = new CheckBox { Content = LanguageManager.Get("TextBold"), IsChecked = initial.Bold,
            Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        shadowSelector = new CheckBox { Content = LanguageManager.Get("TextShadow"), IsChecked = initial.HasShadow,
            Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        flags.Children.Add(boldSelector);
        Grid.SetColumn(shadowSelector, 1);
        flags.Children.Add(shadowSelector);
        Grid.SetRow(flags, 2);
        root.Children.Add(flags);

        var colorSection = new Grid();
        colorSection.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        colorSection.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        Grid.SetRow(colorSection, 3);
        root.Children.Add(colorSection);
        var colorEntry = new Grid();
        colorEntry.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28, GridUnitType.Star) });
        colorEntry.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52, GridUnitType.Star) });
        colorEntry.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20, GridUnitType.Star) });
        colorEntry.Children.Add(new TextBlock { Text = LanguageManager.Get("TextHexColor"),
            Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        hexInput = new TextBox { Text = ToHex(initial.Color), VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Black, Foreground = Brushes.White };
        Grid.SetColumn(hexInput, 1);
        colorEntry.Children.Add(hexInput);
        colorPreview = new Border { Background = new SolidColorBrush(initial.Color), Margin = new Thickness(3) };
        Grid.SetColumn(colorPreview, 2);
        colorEntry.Children.Add(colorPreview);
        colorSection.Children.Add(colorEntry);
        hexInput.TextChanged += (_, _) =>
        {
            if (TryParseHex(hexInput.Text, out var parsed)) SelectColor(parsed, false);
            else colorPreview.BorderBrush = Brushes.Red;
        };

        var palette = new UniformGrid { Columns = 5 };
        Grid.SetRow(palette, 1);
        colorSection.Children.Add(palette);
        foreach (var (key, color) in new (string, Color)[]
        {
            ("ColorWhite", Colors.White), ("ColorBlack", Colors.Black),
            ("ColorBlue", Colors.Blue), ("ColorGreen", Colors.Green),
            ("ColorRed", Colors.Red), ("ColorYellow", Colors.Yellow),
            ("ColorCyan", Colors.Cyan), ("ColorMagenta", Colors.Magenta),
            ("ColorGray", Colors.Gray), ("ColorOrange", Colors.Orange)
        })
        {
            var button = new Button { Content = LanguageManager.Get(key), Background = new SolidColorBrush(color),
                Foreground = new SolidColorBrush(Inverse(color)), Margin = new Thickness(2) };
            button.Click += (_, _) => SelectColor(color, true);
            palette.Children.Add(button);
        }

        shadowSizeSelector = new Slider { Minimum = 0, Maximum = 32,
            Value = initial.ShadowSize, IsMoveToPointEnabled = true,
            VerticalAlignment = VerticalAlignment.Center };
        AddLabeledControl(root, 4, LanguageManager.Get("TextShadowSize"), shadowSizeSelector);
        shadowSizeSelector.IsEnabled = initial.HasShadow;
        shadowSelector.Checked += (_, _) => shadowSizeSelector.IsEnabled = true;
        shadowSelector.Unchecked += (_, _) => shadowSizeSelector.IsEnabled = false;

        var commands = new Grid();
        commands.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        commands.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(commands, 5);
        root.Children.Add(commands);
        var done = new Button { Content = LanguageManager.Get("TextDone"), Margin = new Thickness(2) };
        done.Click += (_, _) =>
        {
            if (!TryParseHex(hexInput.Text, out var parsed))
            {
                hexInput.Focus();
                hexInput.SelectAll();
                return;
            }
            var fontName = fontSelector.Text.Trim();
            if (fontName.Length == 0) { fontSelector.Focus(); return; }
            SelectedSettings = new TextToolSettings(fontName,
                selectedRange ? initial.HeightFraction : sizeSelector.Value / 100, parsed,
                boldSelector.IsChecked == true, shadowSelector.IsChecked == true,
                shadowSizeSelector.Value);
            if (selectedRange) SelectedFontPixels = sizeSelector.Value;
            DialogResult = true;
        };
        commands.Children.Add(done);
        var cancel = new Button { Content = LanguageManager.Get("Cancel"), Margin = new Thickness(2) };
        cancel.Click += (_, _) => DialogResult = false;
        Grid.SetColumn(cancel, 1);
        commands.Children.Add(cancel);
    }

    private static Style CreateFontItemStyle()
    {
        // Windows theme templates can fill hovered/selected popup rows with
        // white independently of ComboBoxItem.Background. Own the row template
        // so every state keeps a dark fill beneath inherited white text.
        var template = new ControlTemplate(typeof(ComboBoxItem));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "FontRowBorder";
        border.SetValue(Border.BackgroundProperty, Brushes.Black);
        border.SetValue(Border.PaddingProperty, new Thickness(
            SystemParameters.WorkArea.Width * .002,
            SystemParameters.WorkArea.Height * .002,
            SystemParameters.WorkArea.Width * .002,
            SystemParameters.WorkArea.Height * .002));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentSourceProperty, "Content");
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        template.VisualTree = border;

        var highlighted = new SolidColorBrush(Color.FromRgb(48, 48, 48));
        var hover = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, highlighted, "FontRowBorder"));
        template.Triggers.Add(hover);
        var selected = new Trigger { Property = ComboBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BackgroundProperty, highlighted, "FontRowBorder"));
        template.Triggers.Add(selected);

        return new Style(typeof(ComboBoxItem))
        {
            Setters =
            {
                new Setter(Control.ForegroundProperty, Brushes.White),
                new Setter(Control.TemplateProperty, template)
            }
        };
    }

    private static void AddLabeledControl(Grid root, int row, string label, UIElement control)
    {
        var group = new Grid();
        group.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38, GridUnitType.Star) });
        group.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62, GridUnitType.Star) });
        group.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1);
        group.Children.Add(control);
        Grid.SetRow(group, row);
        root.Children.Add(group);
    }

    private void SelectColor(Color color, bool updateInput)
    {
        selectedColor = color;
        colorPreview.Background = new SolidColorBrush(color);
        colorPreview.BorderBrush = Brushes.Transparent;
        if (updateInput) hexInput.Text = ToHex(color);
    }

    private static Color Inverse(Color color) => Color.FromRgb((byte)(255 - color.R),
        (byte)(255 - color.G), (byte)(255 - color.B));

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static bool TryParseHex(string raw, out Color color)
    {
        var text = raw.Trim().TrimStart('#');
        if (text.Length == 6 && uint.TryParse(text, NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out var rgb))
        {
            color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            return true;
        }
        color = Colors.Transparent;
        return false;
    }
}
