# Stable Footer Status

The footer keeps one primary status message at its left edge. Ready and image-open results use the base status label. Editing, task progress, errors, and temporary crop recenter completion use the edit status label in the same position. Visibility is mutually exclusive, so changing status does not shift text horizontally.

Both labels contain single-line text blocks with overflow scrolling. The footer height is calculated once from the status font size and label padding, so loading an image or changing a message cannot grow the bar. A temporary recenter-complete message appears for two seconds, then the previous editing status returns.

The crop recenter pill sizes to its own content. Its horizontal alignment prevents a longer crop warning in the same vertical panel from stretching the button.
