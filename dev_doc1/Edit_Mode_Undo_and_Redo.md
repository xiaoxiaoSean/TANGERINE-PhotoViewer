# Edit Mode Undo and Redo

## Behavior

The last two top menu items in edit mode are Undo and Redo. They are hidden outside edit mode and disabled when their respective histories are empty. A new edit clears redo history. History belongs to the current image and is cleared when another image is loaded. Up to 50 completed actions are retained.

One brush or eraser stroke is one action. Only the image tiles touched by that stroke are copied into history. Text box creation, text input, selected text formatting, font size changes, and text box movement or resizing are also recorded. Text documents are serialized to XAML for history so rich text formatting can be restored. Selected run shadow values are captured separately alongside the document.

Undo and redo restore the annotation version associated with each action. This keeps the unsaved status and Save As menu accurate when returning to a previously saved state. The menu is unavailable while drawing or saving. History restoration errors are reported through the existing `StageException` status path with stage code `NOTES0005`; text capture errors use `TEXT0008`.

## Localization and layout

The menu labels are provided in all six UI resource files. The new menu items have no fixed size and use the existing menu layout and styling.
