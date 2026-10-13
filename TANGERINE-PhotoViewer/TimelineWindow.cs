using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TANGERINE_PhotoViewer.DefaultApps;

namespace TANGERINE_PhotoViewer;

/// <summary>
/// A modal history navigator. The window stays open after each jump, so the user can
/// select and visit several points without reopening the top menu. Rows are rebuilt
/// from the live undo and redo stacks after each jump.
/// </summary>
internal sealed class TimelineWindow : Window
{
    internal sealed record Row(string Text, int Position, Brush Foreground);

    private readonly Func<IReadOnlyList<Row>> getRows;
    private readonly Func<int, bool> goTo;
    private readonly ListBox list;
    private readonly Button goButton;

    internal TimelineWindow(Func<IReadOnlyList<Row>> getRows, Func<int, bool> goTo)
    {
        this.getRows = getRows;
        this.goTo = goTo;
        Title = LanguageManager.Get("Timeline");
        Icon = IconAssets.WindowIcon;
        Background = Brushes.Black;
        Foreground = Brushes.White;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = SystemParameters.WorkArea.Width * .48;
        Height = SystemParameters.WorkArea.Height * .62;

        var layout = new Grid { Margin = new Thickness(12) };
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(88, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12, GridUnitType.Star) });
        Content = layout;

        list = new ListBox
        {
            Background = Brushes.Black,
            Foreground = Brushes.White,
            BorderBrush = Brushes.Gray,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        Grid.SetRow(list, 0);
        layout.Children.Add(list);

        goButton = new Button
        {
            Content = LanguageManager.Get("TimelineGoTo"),
            Background = Brushes.Black,
            Foreground = Brushes.White,
            BorderBrush = Brushes.White,
            IsEnabled = false,
            Margin = new Thickness(0, 8, 0, 0)
        };
        goButton.Click += GoButton_Click;
        list.SelectionChanged += (_, _) => goButton.IsEnabled = list.SelectedItem is ListBoxItem;
        Grid.SetRow(goButton, 1);
        layout.Children.Add(goButton);
        RefreshRows();
    }

    private void GoButton_Click(object sender, RoutedEventArgs e)
    {
        if (list.SelectedItem is not ListBoxItem { Tag: int position }) return;
        try
        {
            if (goTo(position)) RefreshRows(position);
        }
        catch (Exception ex)
        {
            var stage = new StageException("TIMELINE0001", LanguageManager.Get("HistoryFailed"), ex); // TIMELINE0001
            MessageBox.Show(this, $"{stage.StageCode}: {stage.Message}",
                LanguageManager.Get("Timeline"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshRows(int? selectedPosition = null)
    {
        var rows = getRows();
        list.Items.Clear();
        ListBoxItem? preferred = null;
        foreach (var row in rows)
        {
            var item = new ListBoxItem
            {
                Content = new TextBlock
                {
                    Text = row.Text,
                    Foreground = row.Foreground,
                    TextWrapping = TextWrapping.Wrap
                },
                Tag = row.Position,
                Background = Brushes.Black
            };
            list.Items.Add(item);
            if (selectedPosition == row.Position ||
                (selectedPosition is null && row.Foreground == Brushes.Red)) preferred = item;
        }
        list.SelectedItem = preferred;
        preferred?.BringIntoView();
    }
}
