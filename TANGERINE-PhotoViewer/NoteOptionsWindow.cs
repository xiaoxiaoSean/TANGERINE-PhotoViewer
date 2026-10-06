using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TANGERINE_PhotoViewer;

/// <summary>
/// Presents a modal, live preview for the pen or eraser. Every control is placed in
/// proportional Grid rows and columns. Diameter is measured in physical display pixels.
/// </summary>
internal sealed class NoteOptionsWindow : Window
{
    private readonly bool chooseColor;
    private readonly Slider diameterSlider;
    private readonly Slider? redSlider;
    private readonly Slider? greenSlider;
    private readonly Slider? blueSlider;
    private readonly Border previewHost;
    private readonly Ellipse previewCircle;
    private readonly TextBlock diameterValue;
    private readonly double dpiScale;

    internal Color SelectedColor { get; private set; }
    internal double SelectedDiameter { get; private set; }

    internal NoteOptionsWindow(bool chooseColor, Color initialColor, double initialDiameter,
        double minimumDiameter, double maximumDiameter, double dpiScale)
    {
        this.chooseColor = chooseColor;
        // The slider and preview express physical monitor pixels. WPF geometry is in
        // device-independent units, so dividing by DPI scale preserves exact screen size.
        this.dpiScale = dpiScale;
        SelectedColor = initialColor;
        SelectedDiameter = initialDiameter;
        Title = LanguageManager.Get(chooseColor ? "BrushOptionsTitle" : "EraserOptionsTitle");
        Background = Brushes.Black;
        Foreground = Brushes.White;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.Manual;
        Width = SystemParameters.WorkArea.Width * 0.34;
        Height = SystemParameters.WorkArea.Height * (chooseColor ? 0.52 : 0.34);

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(chooseColor ? 20 : 25, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(chooseColor ? 24 : 43, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(chooseColor ? 50 : 25, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(chooseColor ? 13 : 14, GridUnitType.Star) });
        Content = root;

        var diameterPanel = new Grid();
        diameterPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        diameterPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        diameterPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30, GridUnitType.Star) });
        diameterPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55, GridUnitType.Star) });
        diameterPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(15, GridUnitType.Star) });
        Grid.SetRow(diameterPanel, 0);
        root.Children.Add(diameterPanel);
        var diameterLabel = new TextBlock
        {
            Text = LanguageManager.Get(chooseColor ? "BrushRadius" : "EraserRadius"),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.White
        };
        diameterPanel.Children.Add(diameterLabel);
        diameterSlider = new Slider
        {
            Minimum = minimumDiameter,
            Maximum = maximumDiameter,
            Value = Math.Clamp(initialDiameter, minimumDiameter, maximumDiameter),
            VerticalAlignment = VerticalAlignment.Center,
            IsMoveToPointEnabled = true
        };
        Grid.SetColumn(diameterSlider, 1);
        diameterPanel.Children.Add(diameterSlider);
        diameterValue = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White };
        Grid.SetColumn(diameterValue, 2);
        diameterPanel.Children.Add(diameterValue);
        var rangeText = string.Format(LanguageManager.Get("DiameterRange"),
            minimumDiameter.ToString("0.##"), maximumDiameter.ToString("0.##"));
        var rangeLabel = new TextBlock { Text = rangeText, Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        Grid.SetRow(rangeLabel, 1);
        Grid.SetColumnSpan(rangeLabel, 3);
        diameterPanel.Children.Add(rangeLabel);

        previewHost = new Border { Background = Brushes.Black, ClipToBounds = true };
        Grid.SetRow(previewHost, 1);
        root.Children.Add(previewHost);
        var previewCanvas = new Canvas();
        previewHost.Child = previewCanvas;
        previewCircle = new Ellipse();
        previewCanvas.Children.Add(previewCircle);

        if (chooseColor)
        {
            var colorPanel = new Grid();
            colorPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            colorPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            colorPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(colorPanel, 2);
            root.Children.Add(colorPanel);
            redSlider = CreateColorSlider(colorPanel, 0, LanguageManager.Get("BrushRed"), initialColor.R);
            greenSlider = CreateColorSlider(colorPanel, 1, LanguageManager.Get("BrushGreen"), initialColor.G);
            blueSlider = CreateColorSlider(colorPanel, 2, LanguageManager.Get("BrushBlue"), initialColor.B);
            redSlider.ValueChanged += (_, _) => UpdatePreview();
            greenSlider.ValueChanged += (_, _) => UpdatePreview();
            blueSlider.ValueChanged += (_, _) => UpdatePreview();
        }

        var buttons = new Grid();
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);
        var accept = new Button { Content = LanguageManager.Get("Apply") };
        accept.Click += (_, _) =>
        {
            SelectedDiameter = diameterSlider.Value;
            SelectedColor = CurrentColor();
            DialogResult = true;
        };
        buttons.Children.Add(accept);
        var cancel = new Button { Content = LanguageManager.Get("Cancel") };
        cancel.Click += (_, _) => DialogResult = false;
        Grid.SetColumn(cancel, 1);
        buttons.Children.Add(cancel);

        diameterSlider.ValueChanged += (_, _) => UpdatePreview();
        previewHost.SizeChanged += (_, _) => UpdatePreview();
        UpdatePreview();
    }

    private static Slider CreateColorSlider(Grid parent, int row, string label, byte initial)
    {
        var group = new Grid();
        group.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30, GridUnitType.Star) });
        group.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70, GridUnitType.Star) });
        Grid.SetRow(group, row);
        parent.Children.Add(group);
        group.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.White });
        var slider = new Slider { Minimum = 0, Maximum = 255, Value = initial,
            IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(slider, 1);
        group.Children.Add(slider);
        return slider;
    }

    private Color CurrentColor() => !chooseColor ? SelectedColor : Color.FromRgb(
        (byte)Math.Round(redSlider!.Value), (byte)Math.Round(greenSlider!.Value),
        (byte)Math.Round(blueSlider!.Value));

    private void UpdatePreview()
    {
        if (previewCircle is null || previewHost is null || diameterValue is null || diameterSlider is null) return;
        var diameterPixels = diameterSlider.Value;
        diameterValue.Text = string.Format(LanguageManager.Get("RadiusPreview"),
            Math.Round(diameterPixels, 1).ToString("0.#"));
        var color = chooseColor ? CurrentColor() : Colors.Black;
        previewCircle.Fill = new SolidColorBrush(color);
        previewHost.Background = chooseColor
            ? new SolidColorBrush(Color.FromRgb((byte)(255 - color.R), (byte)(255 - color.G),
                (byte)(255 - color.B)))
            : Brushes.White;
        // This is the same physical diameter used by the raster brush, independent of
        // image resolution or zoom. Oversized circles clip instead of being rescaled.
        var diameter = diameterPixels / dpiScale;
        previewCircle.Width = diameter;
        previewCircle.Height = diameter;
        Canvas.SetLeft(previewCircle, (previewHost.ActualWidth - diameter) / 2);
        Canvas.SetTop(previewCircle, (previewHost.ActualHeight - diameter) / 2);
    }
}
