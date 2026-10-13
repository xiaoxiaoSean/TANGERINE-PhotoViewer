# Unsaved Edit Confirmation

## When the confirmation appears

The viewer compares the current edit-history version with the last successfully saved version. It treats an edit as unsaved even if an eraser removes the last visible annotation. Undoing back to the saved version makes the image clean again. A confirmation appears before opening another image (including previous/next navigation and drag-and-drop), unloading the file, or closing the main window when edits are unsaved.

The first confirmation offers Yes (Save As), No (discard edits and continue), and Cancel (keep the current image and stop the requested action). Save As is awaited before the action proceeds. Canceling its file dialog or canceling the export keeps the current image open and stops the action. Opening and unloading also recheck the edit version after their asynchronous work, before replacing the image, so an edit made during loading cannot bypass the confirmation.

## Save failure

An actual Save As failure opens a second, action-specific window with explicit buttons. **Retry Save As with a new path** opens a fresh Save As dialog and can repeat until the save succeeds or the user makes another choice. **Discard edits and continue: [action]** proceeds with the original previous-image, next-image, unload, close-window, or open request without saving. **Cancel** keeps the current image and stops the original action.

The export method returns separate Saved, Canceled, and Failed outcomes, so canceling the save dialog does not trigger the failure choices. A save only counts as successful for navigation when the currently displayed edit version was written. Failed setup and export exceptions are reported through the existing stage-code error system.

## Closing behavior

WPF's synchronous `Closing` event is canceled while the asynchronous confirmation and export run. The prompt starts after that event returns, so a discard choice cannot reenter `Window.Close` from inside the first closing call. On an approved continuation, the window closes once with the confirmation bypass flag set. The existing `OnClosed` cleanup then cancels background operations.

All new dialog text is localized in the English, Simplified Chinese, and Traditional Chinese resource files. The failure window uses a work-area percentage for its width rather than a fixed control size.
