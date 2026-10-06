using System.Windows;

namespace TANGERINE_PhotoViewer;

public partial class MainWindow
{
    private readonly Dictionary<string, (string Action, int Percent)> activeTaskProgress = new();
    private readonly Dictionary<string, string> completedTaskProgress = new();
    private string? lastTaskProgressText;

    /// <summary>
    /// One entry represents one independent asynchronous job. Showing a deterministic
    /// ordered snapshot avoids one job overwriting another job's percentage when their
    /// Progress callbacks arrive on the UI dispatcher in alternating order.
    /// </summary>
    private void SetTaskProgress(string key, string action, int percent)
    {
        // A running task stays below 100% until its caller has actually
        // finished and verified its result. The caller explicitly clears
        // old completions when a new user operation begins.
        completedTaskProgress.Remove(key);
        activeTaskProgress[key] = (action, Math.Clamp(percent, 0, 99));
        RefreshTaskProgress();
    }

    private void CompleteTaskProgress(string key, string message)
    {
        activeTaskProgress.Remove(key);
        completedTaskProgress[key] = message;
        RefreshTaskProgress();
    }

    private void ClearTaskProgress(string key)
    {
        activeTaskProgress.Remove(key);
        completedTaskProgress.Remove(key);
        RefreshTaskProgress();
    }

    private void ClearCompletedTaskProgress()
    {
        if (completedTaskProgress.Count == 0) return;
        completedTaskProgress.Clear();
        RefreshTaskProgress();
    }

    private void ClearActiveImageTaskProgress()
    {
        // A new view request supersedes decoding callbacks, but an already
        // finished cache or opening task may remain visible beside the next
        // running image task until the next explicit user operation.
        activeTaskProgress.Remove("open");
        activeTaskProgress.Remove("region");
        activeTaskProgress.Remove("preview");
        activeTaskProgress.Remove("unload");
        RefreshTaskProgress();
    }

    private static string TaskCompletedMessage(string actionKey)
    {
        var action = LanguageManager.Get(actionKey);
        // Chinese action labels describe active work with "正在". A finished
        // task must name the action itself rather than say it is still running.
        if (action.StartsWith("正在", StringComparison.Ordinal)) action = action[2..];
        return string.Format(LanguageManager.Get("TaskCompleted"), action);
    }

    private void RefreshTaskProgress()
    {
        if (activeTaskProgress.Count == 0 && completedTaskProgress.Count == 0)
        {
            lastTaskProgressText = null;
            // The center label is reserved for a final result or error while
            // the left label owns all active task percentages. Restore the
            // center label only after the final progress entry disappears.
            StatusLabel.Visibility = Visibility.Visible;
            UpdateNoteStatus();
            return;
        }
        StatusLabel.Visibility = Visibility.Collapsed;
        var parts = activeTaskProgress
            .Select(entry => (entry.Key, Text: $"{entry.Value.Action} {entry.Value.Percent}%"))
            .Concat(completedTaskProgress.Select(entry => (entry.Key, Text: entry.Value)))
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => entry.Text);
        var value = string.Join("|", parts);
        if (value == lastTaskProgressText) return;
        lastTaskProgressText = value;
        EditStatusLabel.Visibility = Visibility.Visible;
        EditStatusLabel.Content = value;
    }
}
