using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using TANGERINE_PhotoViewer.DefaultApps;

namespace TANGERINE_PhotoViewer;

public partial class MainWindow
{
    private sealed record TimelineStamp(DateTimeOffset Time, int Number);
    private sealed record TimelineMarker(TimelineStamp Stamp, string ActionKey, string? Argument);
    private sealed record NoteHistoryEntry(Action Undo, Action Redo, long BeforeVersion,
        long AfterVersion, TimelineStamp Stamp, string ActionKey);
    private sealed record TileState(byte[] Pixels, int Occupied);
    private sealed record TextState(string Document, Rect Bounds, double FontPixels,
        double LayoutScale, double[] RunShadows);

    private readonly List<NoteHistoryEntry> undoHistory = [];
    private readonly List<NoteHistoryEntry> redoHistory = [];
    private readonly List<TimelineMarker> openingTimeline = [];
    private readonly Dictionary<(int X, int Y), TileState?> strokeBefore = [];
    private readonly Dictionary<TextNote, TextState> textHistoryStates = [];
    private long historyVersionCounter;
    private long currentHistoryVersion;
    private bool restoringHistory;
    private bool recordingStroke;
    private TimelineStamp? lastTimelineStamp;

    // Keep the history bounded while storing only tiles touched by a stroke.
    private static DateTimeOffset TimelineNow() => DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8));

    private TimelineStamp NextTimelineStamp(DateTimeOffset time)
    {
        var second = time.ToUnixTimeSeconds();
        var number = lastTimelineStamp?.Time.ToUnixTimeSeconds() == second
            ? lastTimelineStamp.Number + 1 : 1;
        return lastTimelineStamp = new TimelineStamp(time, number);
    }

    private static string TimelineText(TimelineStamp stamp, string key, string? argument = null)
    {
        var time = stamp.Time.ToString("yyyy/MM/dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        if (stamp.Number > 1) time += $"({stamp.Number})";
        var action = LanguageManager.Get(key);
        if (argument is not null) action = string.Format(action, argument);
        return $"{time} {action}";
    }

    private void RecordNoteHistory(Action undo, Action redo, string actionKey)
    {
        var before = currentHistoryVersion;
        var after = ++historyVersionCounter;
        undoHistory.Add(new NoteHistoryEntry(undo, redo, before, after,
            NextTimelineStamp(TimelineNow()), actionKey));
        if (undoHistory.Count > 50)
        {
            var discarded = undoHistory[0];
            undoHistory.RemoveAt(0);
            // The oldest reachable state now follows the discarded action.
            openingTimeline.Clear();
            openingTimeline.Add(new TimelineMarker(discarded.Stamp, "TimelineEarlierHistory", null));
        }
        redoHistory.Clear();
        openingTimeline.Clear();
        lastTimelineStamp = null;
        currentHistoryVersion = notesVersion = after;
        UpdateHistoryMenu();
    }

    private void RecordOpenedTimeline(DateTimeOffset started, string file)
    {
        var name = System.IO.Path.GetFileName(file);
        openingTimeline.Add(new TimelineMarker(NextTimelineStamp(started), "TimelineOpenStarted", name));
        openingTimeline.Add(new TimelineMarker(NextTimelineStamp(TimelineNow()), "TimelineOpenCompleted", name));
    }

    private void ResetNoteHistory()
    {
        undoHistory.Clear();
        redoHistory.Clear();
        strokeBefore.Clear();
        textHistoryStates.Clear();
        recordingStroke = false;
        currentHistoryVersion = notesVersion;
        historyVersionCounter = Math.Max(historyVersionCounter, notesVersion);
        UpdateHistoryMenu();
    }

    private void UpdateHistoryMenu()
    {
        if (UndoItem is null) return;
        UndoItem.Visibility = RedoItem.Visibility = TimelineItem.Visibility =
            editingNotes ? Visibility.Visible : Visibility.Collapsed;
        UndoItem.IsEnabled = editingNotes && !drawingNote && !savingNotes && undoHistory.Count > 0;
        RedoItem.IsEnabled = editingNotes && !drawingNote && !savingNotes && redoHistory.Count > 0;
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => RunNoteHistory(undoHistory, redoHistory, true);
    private void Redo_Click(object sender, RoutedEventArgs e) => RunNoteHistory(redoHistory, undoHistory, false);

    private void Timeline_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var window = new TimelineWindow(GetTimelineRows, GoToTimelinePosition) { Owner = this };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            ShowNotesError(new StageException("TIMELINE0002", LanguageManager.Get("HistoryFailed"), ex)); // TIMELINE0002
        }
    }

    private IReadOnlyList<TimelineWindow.Row> GetTimelineRows()
    {
        var rows = new List<TimelineWindow.Row>();
        foreach (var marker in openingTimeline)
            rows.Add(new TimelineWindow.Row(TimelineText(marker.Stamp, marker.ActionKey, marker.Argument),
                0, undoHistory.Count == 0 && marker == openingTimeline[^1]
                    ? Brushes.Red : Brushes.White));
        var allActions = undoHistory.Concat(redoHistory.AsEnumerable().Reverse()).ToArray();
        for (var index = 0; index < allActions.Length; index++)
        {
            var entry = allActions[index];
            var color = index >= undoHistory.Count ? Brushes.Gray
                : index == undoHistory.Count - 1 ? Brushes.Red : Brushes.White;
            rows.Add(new TimelineWindow.Row(TimelineText(entry.Stamp, entry.ActionKey), index + 1, color));
        }
        return rows;
    }

    private bool GoToTimelinePosition(int position)
    {
        if (!editingNotes || drawingNote || savingNotes) return false;
        while (undoHistory.Count > position)
        {
            var count = undoHistory.Count;
            RunNoteHistory(undoHistory, redoHistory, true);
            if (undoHistory.Count == count) return false;
        }
        while (undoHistory.Count < position)
        {
            var count = undoHistory.Count;
            RunNoteHistory(redoHistory, undoHistory, false);
            if (undoHistory.Count == count) return false;
        }
        return true;
    }

    private void RunNoteHistory(List<NoteHistoryEntry> sourceHistory,
        List<NoteHistoryEntry> destinationHistory, bool undo)
    {
        if (!editingNotes || drawingNote || savingNotes || sourceHistory.Count == 0) return;
        var entry = sourceHistory[^1];
        try
        {
            restoringHistory = true;
            if (undo) entry.Undo(); else entry.Redo();
            sourceHistory.RemoveAt(sourceHistory.Count - 1);
            destinationHistory.Add(entry);
            currentHistoryVersion = notesVersion = undo ? entry.BeforeVersion : entry.AfterVersion;
            noteStatusResult = false;
            RefreshMenu();
            UpdateNoteStatus();
        }
        catch (Exception ex)
        {
            ShowNotesError(new StageException("NOTES0005", LanguageManager.Get("HistoryFailed"), ex)); // NOTES0005
        }
        finally
        {
            restoringHistory = false;
            UpdateHistoryMenu();
        }
    }

    private void BeginNoteStroke()
    {
        strokeBefore.Clear();
        recordingStroke = true;
        UpdateHistoryMenu();
    }

    private void CaptureTileBefore((int X, int Y) key, NoteTile? tile)
    {
        if (recordingStroke && !strokeBefore.ContainsKey(key))
            strokeBefore[key] = tile is null ? null : new TileState((byte[])tile.Pixels.Clone(), tile.Occupied);
    }

    private void FinishNoteStroke()
    {
        recordingStroke = false;
        if (strokeBefore.Count == 0) { UpdateHistoryMenu(); return; }
        var before = new Dictionary<(int X, int Y), TileState?>(strokeBefore);
        var after = before.Keys.ToDictionary(key => key, key => SnapshotTile(key));
        strokeBefore.Clear();
        RecordNoteHistory(() => RestoreTiles(before), () => RestoreTiles(after),
            noteTool == NoteTool.Brush ? "TimelineBrush" : "TimelineEraser");
    }

    private TileState? SnapshotTile((int X, int Y) key) =>
        noteTiles.TryGetValue(key, out var tile)
            ? new TileState((byte[])tile.Pixels.Clone(), tile.Occupied) : null;

    private void RestoreTiles(Dictionary<(int X, int Y), TileState?> states)
    {
        foreach (var (key, state) in states)
        {
            if (noteTiles.Remove(key, out var oldTile)) NotesCanvas.Children.Remove(oldTile.View);
            if (state is null) continue;
            var width = Math.Min(NoteTileSize, imageWidth - key.X * NoteTileSize);
            var height = Math.Min(NoteTileSize, imageHeight - key.Y * NoteTileSize);
            var tile = new NoteTile(width, height) { Occupied = state.Occupied };
            Array.Copy(state.Pixels, tile.Pixels, state.Pixels.Length);
            tile.Bitmap.WritePixels(new Int32Rect(0, 0, width, height), tile.Pixels, width * 4, 0);
            tile.View.Source = tile.Bitmap;
            tile.View.Width = width;
            tile.View.Height = height;
            noteTiles.Add(key, tile);
            NotesCanvas.Children.Add(tile.View);
            PositionNoteTile(key.X, key.Y, tile);
        }
    }

    private static TextState SnapshotTextState(TextNote note) =>
        new(XamlWriter.Save(note.Editor.Document), note.ReferenceBounds, note.FontPixels,
            note.LayoutScale, GetTextRuns(note).Select(run =>
                run.ReadLocalValue(RunShadowSizeProperty) is double size ? size : double.NaN).ToArray());

    private static IEnumerable<System.Windows.Documents.Run> GetTextRuns(TextNote note) =>
        note.Editor.Document.Blocks.OfType<System.Windows.Documents.Paragraph>()
            .SelectMany(paragraph => paragraph.Inlines.Cast<System.Windows.Documents.Inline>())
            .SelectMany(DescendantRuns);

    private void RecordTextMutation(TextNote note, TextState before, string actionKey = "TimelineTextEdited")
    {
        var after = SnapshotTextState(note);
        textHistoryStates[note] = after;
        if (before.Document == after.Document && before.Bounds == after.Bounds &&
            before.FontPixels == after.FontPixels && before.LayoutScale == after.LayoutScale &&
            before.RunShadows.SequenceEqual(after.RunShadows))
        {
            notesVersion = currentHistoryVersion;
            return;
        }
        RecordNoteHistory(() => RestoreTextState(note, before),
            () => RestoreTextState(note, after), actionKey);
    }

    private void RestoreTextState(TextNote note, TextState state)
    {
        note.Editor.Document = (System.Windows.Documents.FlowDocument)XamlReader.Parse(state.Document);
        note.ReferenceBounds = state.Bounds;
        note.FontPixels = state.FontPixels;
        note.LayoutScale = state.LayoutScale;
        foreach (var (run, size) in GetTextRuns(note).Zip(state.RunShadows))
            if (!double.IsNaN(size)) run.SetValue(RunShadowSizeProperty, size);
        PositionTextNote(note);
        textHistoryStates[note] = state;
        note.ShadowPreviewDelay?.Stop();
        note.ShadowPreviewDelay?.Start();
    }

    private void RecordTextCreation(TextNote note)
    {
        textHistoryStates[note] = SnapshotTextState(note);
        RecordNoteHistory(() => SetTextNotePresent(note, false),
            () => SetTextNotePresent(note, true), "TimelineTextCreated");
    }

    private void SetTextNotePresent(TextNote note, bool present)
    {
        if (present)
        {
            if (!textNotes.Contains(note)) textNotes.Add(note);
            NotesCanvas.Children.Add(note.Editor);
            if (note.Frame is not null) NotesCanvas.Children.Add(note.Frame);
            if (note.InverseFrame is not null) NotesCanvas.Children.Add(note.InverseFrame);
            note.Editor.IsHitTestVisible = noteTool == NoteTool.Text;
            PositionTextNote(note);
        }
        else
        {
            note.ShadowPreviewDelay?.Stop();
            if (ReferenceEquals(selectedTextNote, note)) selectedTextNote = null;
            SetHoveredTextNote(null);
            textNotes.Remove(note);
            NotesCanvas.Children.Remove(note.Editor);
            if (note.Frame is not null) NotesCanvas.Children.Remove(note.Frame);
            if (note.InverseFrame is not null) NotesCanvas.Children.Remove(note.InverseFrame);
            if (note.SelectionShadowPreview is not null)
                NotesCanvas.Children.Remove(note.SelectionShadowPreview);
            note.SelectionShadowPreview = null;
        }
        UpdateTextSelectionMenu();
    }
}
