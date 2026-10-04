using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;

namespace TANGERINE_PhotoViewer;

public sealed class GifWindow : Window
{
    private readonly string path;
    private readonly Label status = new() { Foreground = System.Windows.Media.Brushes.White };
    private readonly Button export = new();
    private readonly Button stop = new();
    private CancellationTokenSource? operation;
    private bool closing;
    public bool IsWorking => operation is not null;
    public event EventHandler? WorkStateChanged;
    public event EventHandler<int>? ProgressChanged;
    public void StopWork() => operation?.Cancel();

    public GifWindow(string path)
    {
        this.path = path;
        Title = LanguageManager.Get("GifTools");
        Icon = IconAssets.WindowIcon;
        Width = 400; Height = 170; Background = System.Windows.Media.Brushes.Black; Foreground = System.Windows.Media.Brushes.White;
        var panel = new StackPanel { Margin = new Thickness(12) };
        export.Content = LanguageManager.Get("ExportFrames");
        stop.Content = LanguageManager.Get("Stop");
        status.Content = LanguageManager.Get("Ready");
        export.Click += Export_Click;
        stop.Click += (_, _) => operation?.Cancel();
        panel.Children.Add(export); panel.Children.Add(stop); panel.Children.Add(status);
        Content = panel;
        Closing += (_, _) => { closing = true; StopWork(); };
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = LanguageManager.Get("ChooseExportFolder") };
        if (dialog.ShowDialog(this) != true) return;
        operation = new CancellationTokenSource();
        var current = operation;
        WorkStateChanged?.Invoke(this, EventArgs.Empty);
        export.IsEnabled = false;
        try
        {
            var progress = new Progress<int>(value =>
            {
                if (closing || !ReferenceEquals(operation, current)) return;
                status.Content = string.Format(LanguageManager.Get("ExportProgress"), value);
                ProgressChanged?.Invoke(this, value);
            });
            var count = await Task.Run(() => NativeWork.Run(() => ImageLoader.ExportGif(path, dialog.FolderName, current.Token, progress), current.Token), current.Token);
            if (!closing) status.Content = string.Format(LanguageManager.Get("ExportedFrames"), count);
        }
        catch (OperationCanceledException) { if (!closing) status.Content = LanguageManager.Get("Stopped"); }
        catch (Exception ex) { if (!closing) status.Content = string.Format(LanguageManager.Get("OperationFailed"), ex.Message); }
        finally
        {
            current.Dispose();
            if (ReferenceEquals(operation, current)) operation = null;
            if (!closing) export.IsEnabled = true;
            WorkStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}








