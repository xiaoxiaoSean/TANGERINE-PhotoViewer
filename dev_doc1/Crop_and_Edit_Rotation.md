# Crop and Edit Rotation

## Overview

The main menu shows **Crop** and **Rotate (Edit)** immediately after **Notes** when an image is loaded. Either item enters the existing editing mode. Both tools also appear in **Switch Tool**.

## Crop interaction

- Entering the tool starts with one selection covering the whole image. Drag any of its four visible corner handles to resize that same selection. Ctrl + left-button drag inside the selection moves it; clicking elsewhere does not create another selection.
- The unselected image is shaded, while a white outline marks the output region. The shade and outline are display only.
- A corner cannot leave the image. If the pointer is dragged beyond an image edge, the corner is clamped to that edge and a localized lower-left warning appears. The warning remains briefly after release.
- The bottom-left pill button animates the selection's scale and screen position to the viewport center. A 260 ms cubic ease accelerates and then decelerates; a localized "Recenter complete" message appears in the left side of the fixed footer after the final frame. The normal zoom slider, Ctrl + wheel, middle-button panning, and Space + left-button panning remain available.
- The crop rectangle is stored in oriented image pixels, not screen coordinates. Zooming and panning do not modify it. The initial full-image selection is equivalent to no crop and does not dirty the document. A committed corner or move drag adds one undo/timeline entry; an interrupted drag restores the previous rectangle.

## Rotation interaction

The rotation editing tool shows bottom-left buttons for 90-degree left and right rotation. Each click records an edit-history entry. Undo and redo restore the image orientation and transform the crop selection with it. The version checkpoint remains stable during history replay so an edited rotation stays marked as unsaved.

## Export

Save As composites brush and text annotations in source coordinates, applies the selected orientation, then crops the oriented result. The libvips path keeps this operation lazy, which avoids allocating a full-resolution intermediate bitmap. The ImageMagick fallback crops after rotation and resets the image page origin. Multi-frame files crop every oriented frame and preserve their frame count. Output dimensions are verified against the crop rectangle before the temporary file replaces the destination.

The crop is part of the existing edit history and unsaved-change flow. Starting a new edit after undo discards redo entries, including crop and rotation entries.

Crop and edit rotation are top-level editing tools outside the Notes menu. Their clean-state message says there are no unsaved **edits**, so it also applies before a crop or edit rotation has changed the image.

## Localization and layout

Menu labels, the pill button, and timeline actions use resource keys in the default, English, Simplified Chinese, and Traditional Chinese resource sets. New controls use content-based sizing. The pill radius follows half of its actual height; the global corner-radius handler excludes this deliberately pill-shaped border.

## Verification

`dotnet build TANGERINE-PhotoViewer.slnx --no-restore` completed with zero warnings and zero errors.
