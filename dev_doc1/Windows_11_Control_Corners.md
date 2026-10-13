# Windows 11 Control Corners

## Radius calculation

The application uses a custom resolution-aware extension of the Windows 11 rounded-corner scale. The baseline radius is 8 WPF device-independent pixels (DIPs) on a 1920 × 1080 monitor at 100% display scaling. For a monitor with physical pixel width `W`, height `H`, and Windows display scale `S`, the radius is:

`radius in DIPs = 8 × sqrt((W × H) / (1920 × 1080))`

`radius in physical pixels = radius in DIPs × S`

For example, 1920 × 1080 at 150% gives 12 physical pixels, 3840 × 2160 at 100% gives 16 physical pixels, and 3840 × 2160 at 200% gives 32 physical pixels. The square-root area ratio handles landscape, portrait, and ultrawide displays without relying on one axis. WPF performs the final DPI multiplication; the application does not multiply the DIP value by DPI a second time.

Microsoft's Windows 11 software rounding itself has an 8-pixel baseline at 96 DPI and scales linearly with the display scaling factor. The additional resolution factor above is specific to this application's requested behavior. References: [Geometry in Windows 11](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/geometry) and [Guidance for Rounded Display Bezels](https://learn.microsoft.com/en-us/windows-hardware/design/component-guidelines/guidance-for-rounded-display-bezels).

## Implementation

`RoundedControlCorners` registers loaded class handlers before the first window opens. They apply the computed radius to `Border` surfaces and `Rectangle` tracks, including elements created later by menus, tooltips, and other control templates. The image display and navigator frames are excluded.

Buttons require their visible template border to be rounded. The application-wide button template now gives its border a rounded baseline, and the System Operations window's separate action-button template does the same. The loaded handler then applies the monitor-specific radius to those borders. The handler uses the actual `Border` or `Rectangle` sender instead of rejecting an event because its `OriginalSource` belongs to a nested template element. A runtime check opened the System Operations window and confirmed that all six action buttons had template borders with a positive corner radius.

Each WPF `HwndSource` tracks its own monitor. Win32 `MonitorFromWindow` selects the monitor containing most of the window; `GetMonitorInfoW` provides its physical pixel bounds. On window position, display configuration, or DPI changes, a dispatcher callback checks the new resolution and reapplies the radius to the visual tree when its value changes. The `PerMonitorV2` application manifest and WPF runtime setting enable the display-scale part of the calculation when a window crosses monitors. If monitor information is temporarily unavailable, the baseline radius remains in use.

Microsoft references: [MonitorFromWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-monitorfromwindow), [GetMonitorInfoW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getmonitorinfow), [WM_DPICHANGED](https://learn.microsoft.com/en-us/windows/win32/hidpi/wm-dpichanged), and [Setting the default DPI awareness for a process](https://learn.microsoft.com/en-us/windows/win32/hidpi/setting-the-default-dpi-awareness-for-a-process).

No control dimensions or user-facing strings were added. The existing templates and interactions remain in place.
