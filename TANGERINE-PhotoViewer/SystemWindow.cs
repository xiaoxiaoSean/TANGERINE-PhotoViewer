using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TANGERINE_PhotoViewer.DefaultApps;

namespace TANGERINE_PhotoViewer;

public sealed class SystemWindow : Window
{
    private readonly StackPanel choices = new();
    private readonly TextBlock status = new() { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    private readonly Button automatic = new();
    private readonly Button settings = new();
    private readonly Button selectAll = new();
    private readonly Button selectNone = new();
    private readonly Button unregister = new();
    private readonly Button stop = new();
    private CancellationTokenSource? operation;
    private bool closeRequested;

    public void RequestClose()
    {
        closeRequested = true;
        if (operation is null) Close();
        else operation.Cancel();
    }

    public SystemWindow()
    {
        Title = LanguageManager.Get("SystemOperations");
        Icon = IconAssets.WindowIcon;
        Width = 650; Height = 560; Background = Brushes.Black; Foreground = Brushes.White;
        var root = new DockPanel { Margin = new Thickness(12) };
        var actions = new StackPanel();
        var selectionActions = new StackPanel { Orientation = Orientation.Horizontal };
        selectAll.Content = LanguageManager.Get("DefaultAppsSelectAll");
        selectNone.Content = LanguageManager.Get("DefaultAppsSelectNone");
        selectAll.Click += (_, _) => SelectAll(true);
        selectNone.Click += (_, _) => SelectAll(false);
        selectionActions.Children.Add(selectAll);
        selectionActions.Children.Add(selectNone);
        actions.Children.Add(selectionActions);
        var operationActions = new WrapPanel();
        automatic.Content = LanguageManager.Get("DefaultAppsUseThisApp");
        settings.Content = LanguageManager.Get("OpenDefaultApps");
        unregister.Content = LanguageManager.Get("DefaultAppsUnregisterAll");
        stop.Content = LanguageManager.Get("Stop");
        automatic.Click += async (_, _) => await RegisterAsync(true);
        settings.Click += async (_, _) => await RegisterAsync(false);
        unregister.Click += async (_, _) => await UnregisterAsync();
        stop.Click += (_, _) => operation?.Cancel();
        operationActions.Children.Add(automatic);
        operationActions.Children.Add(settings);
        operationActions.Children.Add(unregister);
        operationActions.Children.Add(stop);
        actions.Children.Add(operationActions);
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        choices.Children.Add(new TextBlock { Text = LanguageManager.Get("DefaultAppsDescription"), Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap });
        foreach (var format in DefaultAppAssociationService.Formats)
            choices.Children.Add(new CheckBox { Content = $"{format.Label} ({string.Join(", ", format.Extensions)})", Tag = format, Foreground = Brushes.White, IsChecked = false });
        scroll.Content = choices;
        root.Children.Add(scroll);
        Content = root;
        status.Text = LanguageManager.Get("Ready");
        SetBusy(false);
        Closing += (_, e) => { if (operation is not null) { e.Cancel = true; closeRequested = true; operation.Cancel(); status.Text = LanguageManager.Get("DefaultAppsWait"); } };
    }

    private void SelectAll(bool selected)
    {
        if (operation is not null) return;
        foreach (var choice in choices.Children.OfType<CheckBox>()) choice.IsChecked = selected;
    }

    private void SetBusy(bool busy)
    {
        automatic.IsEnabled = settings.IsEnabled = unregister.IsEnabled = !busy;
        selectAll.IsEnabled = selectNone.IsEnabled = choices.IsEnabled = !busy;
        stop.IsEnabled = busy;
    }

    private void ShowUnregisterProgress(string message)
    {
        if (operation is not null) status.Text = message;
    }

    private async Task UnregisterAsync()
    {
        if (operation is not null) return;
        operation = new CancellationTokenSource();
        var current = operation;
        SetBusy(true);
        status.Text = LanguageManager.Get("DefaultAppsUnregisterWorking");
        try
        {
            IProgress<string> progress = new Progress<string>(ShowUnregisterProgress);
            var count = await Task.Run(() => DefaultAppUnregistrationService.Unregister(progress.Report, current.Token));
            status.Text = string.Format(LanguageManager.Get("DefaultAppsUnregisterCompleted"), count);
        }
        catch (OperationCanceledException) { status.Text = LanguageManager.Get("Stopped"); }
        catch (Exception ex) { status.Text = string.Format(LanguageManager.Get("OperationFailed"), ex.Message); }
        finally
        {
            current.Dispose();
            operation = null;
            SetBusy(false);
            if (closeRequested) Close();
        }
    }

    private async Task RegisterAsync(bool selectDefault)
    {
        if (operation is not null) return;
        var selected = choices.Children.OfType<CheckBox>().Where(x => x.IsChecked == true)
            .SelectMany(x => ((AssociationFormat)x.Tag).Extensions).ToArray();
        if (selected.Length == 0) { status.Text = LanguageManager.Get("DefaultAppsChooseFormats"); return; }
        operation = new CancellationTokenSource();
        var current = operation;
        SetBusy(true);
        try
        {
            var exe = await Task.Run(DefaultAppAssociationService.GetExecutablePath, current.Token);
            for (var i = 0; i < selected.Length; i++)
            {
                current.Token.ThrowIfCancellationRequested();
                var extension = selected[i];
                status.Text = string.Format(LanguageManager.Get("DefaultAppsProgress"), i + 1, selected.Length, extension);
                if (selectDefault)
                    await Task.Run(() => DefaultAppAssociationService.SetThisAppDefault(extension, exe), current.Token);
                else
                    await Task.Run(() => DefaultAppAssociationService.RegisterHandler(extension, exe), current.Token);
                current.Token.ThrowIfCancellationRequested();
            }
            if (!selectDefault)
                await Task.Run(() => DefaultAppAssociationService.OpenWindowsSettings(true), current.Token);
            status.Text = LanguageManager.Get(selectDefault ? "DefaultAppsCompleted" : "DefaultAppsSettingsOpened");
        }
        catch (OperationCanceledException) { status.Text = LanguageManager.Get("Stopped"); }
        catch (Exception ex) { status.Text = string.Format(LanguageManager.Get("OperationFailed"), ex.Message); }
        finally
        {
            current.Dispose(); operation = null;
            SetBusy(false);
            if (closeRequested) Close();
        }
    }
}
