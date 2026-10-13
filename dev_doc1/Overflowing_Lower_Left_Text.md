# Overflowing Lower-Left Text

The ready/open status, edit status, and crop-tip messages are clipped to the current available width. When a message fits, it remains still. When it exceeds the available space, `OverflowTextMarquee` scrolls the entire line left so every part becomes visible.

The animation uses Unicode text elements. Each Chinese character takes 200 ms to pass the viewport edge, and each Latin character takes 100 ms. Other characters use 100 ms. After the last character disappears, the line remains hidden for 500 ms before the next pass begins.

A text change stops the active pass and starts the replacement text at the beginning immediately. Resizing or hiding the viewport stops and recalculates the pass. The class detaches its rendering and property-change handlers when the window closes.
