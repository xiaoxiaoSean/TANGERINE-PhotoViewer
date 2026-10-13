# Edit Timeline

## User interface

Timeline is the final top menu item in edit mode, following Undo and Redo. It opens a modal window with a chronological list and a **Go there** button below the list. The window stays open after a jump, allowing repeated moves through the history.

The most recent applied edit is red, older applied edits are white, and undone edits that can still be redone are gray. If no edit is applied, the opening or history baseline is red. Each row uses `yyyy/MM/dd HH:mm:ss` in China Standard Time. Actions in the same second receive `(2)`, `(3)`, and so on after the first row. The suffix is assigned when the action occurs and does not change when the user moves through history.

## History behavior

Each row points to a position in the existing undo and redo stacks. Choosing a row and pressing **Go there** performs the required undo or redo operations in sequence. New edits after undo discard the old redo branch, so those gray rows disappear. The existing 50-action memory bound remains in effect. When older actions are discarded, the opening markers are replaced with an earlier-history baseline, which accurately represents the oldest reachable state.

The timeline belongs to the current image. A successful image opening records start and completion markers and begins a new timeline. Both opening markers point to the new image's initial annotation state; image loading itself is not reversed by the edit history. A new image or unload clears the previous image's edit history.

Brush and eraser strokes, text creation and editing, selected text formatting, font size changes, and text box movement or resizing have localized action names. The window and action names are localized in all six UI resource files. The window dimensions use a percentage of the available screen; the list and button use proportional rows. Timeline UI errors use stage codes `TIMELINE0001` and `TIMELINE0002`.
