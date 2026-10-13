using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TANGERINE_PhotoViewer;

/// <summary>
/// Scrolls a clipped single-line message one visible text element at a time.
/// Chinese elements cross the viewport edge in 200 ms; Latin elements use
/// 100 ms. A completed pass stays hidden for 500 ms before starting again.
/// </summary>
internal sealed class OverflowTextMarquee : IDisposable
{
    private readonly TextBlock text;
    private readonly FrameworkElement viewport;
    private readonly Func<double> availableWidth;
    private readonly TranslateTransform offset = new();
    private readonly Stopwatch clock = new();
    private readonly DependencyPropertyDescriptor? textDescriptor;
    private readonly List<(double EndTime, double EndX)> steps = [];
    private double duration;
    private bool running;

    internal OverflowTextMarquee(TextBlock text, FrameworkElement viewport,
        Func<double> availableWidth)
    {
        this.text = text;
        this.viewport = viewport;
        this.availableWidth = availableWidth;
        text.RenderTransform = offset;
        textDescriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty,
            typeof(TextBlock));
        textDescriptor?.AddValueChanged(text, TextChanged);
        viewport.SizeChanged += SizeChanged;
        viewport.IsVisibleChanged += VisibilityChanged;
        viewport.Loaded += Loaded;
        Restart();
    }

    private void TextChanged(object? sender, EventArgs e) => Restart();
    private void SizeChanged(object sender, SizeChangedEventArgs e) => Restart();
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => Restart();
    private void Loaded(object sender, RoutedEventArgs e) => Restart();

    internal void Restart()
    {
        Stop();
        offset.X = 0;
        steps.Clear();
        duration = 0;
        var value = text.Text;
        if (string.IsNullOrEmpty(value) || !viewport.IsVisible || availableWidth() <= 0)
            return;
        var pixelsPerDip = VisualTreeHelper.GetDpi(text).PixelsPerDip;
        double Measure(string content) => new FormattedText(content,
            CultureInfo.CurrentUICulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight,
                text.FontStretch), text.FontSize, text.Foreground, pixelsPerDip)
            .WidthIncludingTrailingWhitespace;
        var fullWidth = Measure(value);
        // The full line must remain laid out behind the clipped viewport;
        // otherwise WPF would clip the TextBlock before it can be translated.
        text.Width = fullWidth;
        if (fullWidth <= availableWidth() + 0.5) return;

        var enumerator = StringInfo.GetTextElementEnumerator(value);
        var prefix = new StringBuilder();
        while (enumerator.MoveNext())
        {
            var element = (string)enumerator.Current!;
            prefix.Append(element);
            var rune = Rune.GetRuneAt(element, 0);
            var cjk = rune.Value is >= 0x2E80 and <= 0x9FFF or
                >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x3134F;
            duration += cjk ? 0.2 : 0.1;
            steps.Add((duration, Measure(prefix.ToString())));
        }
        clock.Restart();
        CompositionTarget.Rendering += Render;
        running = true;
    }

    private void Render(object? sender, EventArgs e)
    {
        var elapsed = clock.Elapsed.TotalSeconds;
        if (elapsed >= duration + 0.5)
        {
            clock.Restart();
            elapsed = 0;
        }
        if (elapsed >= duration)
        {
            offset.X = -text.Width;
            return;
        }
        var previousTime = 0d;
        var previousX = 0d;
        foreach (var (endTime, endX) in steps)
        {
            if (elapsed <= endTime)
            {
                var fraction = (elapsed - previousTime) / (endTime - previousTime);
                offset.X = -(previousX + (endX - previousX) * fraction);
                return;
            }
            previousTime = endTime;
            previousX = endX;
        }
    }

    private void Stop()
    {
        if (running) CompositionTarget.Rendering -= Render;
        running = false;
        clock.Stop();
    }

    public void Dispose()
    {
        Stop();
        textDescriptor?.RemoveValueChanged(text, TextChanged);
        viewport.SizeChanged -= SizeChanged;
        viewport.IsVisibleChanged -= VisibilityChanged;
        viewport.Loaded -= Loaded;
    }
}
