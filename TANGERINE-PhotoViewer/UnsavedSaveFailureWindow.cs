using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TANGERINE_PhotoViewer.DefaultApps;

namespace TANGERINE_PhotoViewer;

/// <summary>
/// Presents explicit, localized choices after Save As fails during an action
/// that would otherwise discard the current image's unsaved edits.
/// </summary>
internal sealed class UnsavedSaveFailureWindow : Window
{
    internal enum Choice { Cancel, Retry, Discard }

    internal Choice SelectedChoice { get; private set; } = Choice.Cancel;

    internal UnsavedSaveFailureWindow(string actionName)
    {
        Title = LanguageManager.Get("AppTitle");
        Icon = IconAssets.WindowIcon;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Black;
        Foreground = Brushes.White;
        Width = SystemParameters.WorkArea.Width * .42;
        SizeToContent = SizeToContent.Height;

        var root = new DockPanel
        {
            Margin = new Thickness(SystemParameters.WorkArea.Width * .012)
        };
        var actions = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, SystemParameters.WorkArea.Height * .012, 0, 0)
        };
        actions.Children.Add(CreateButton(LanguageManager.Get("UnsavedRetrySaveAs"), Choice.Retry));
        actions.Children.Add(CreateButton(string.Format(
            LanguageManager.Get("UnsavedDiscardAndContinue"), actionName), Choice.Discard));
        actions.Children.Add(CreateButton(LanguageManager.Get("Cancel"), Choice.Cancel));
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);
        root.Children.Add(new TextBlock
        {
            Text = LanguageManager.Get("UnsavedSaveFailedPrompt"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.White
        });
        Content = root;
    }

    private Button CreateButton(string label, Choice choice)
    {
        var button = new Button
        {
            Content = label,
            Margin = new Thickness(SystemParameters.WorkArea.Width * .003),
            IsDefault = choice == Choice.Retry,
            IsCancel = choice == Choice.Cancel
        };
        button.Click += (_, _) =>
        {
            SelectedChoice = choice;
            DialogResult = true;
        };
        return button;
    }
}
