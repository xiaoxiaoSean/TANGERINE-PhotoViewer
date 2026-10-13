# System Button Color Consistency

The System Operations window now gives every action button one shared black background, white text, and white border template. WPF's default disabled-button colors previously made the Stop action appear gray while the other actions used the system theme. The shared template keeps colors consistent in both enabled and disabled states; `SetBusy` still controls which actions can be clicked.

The window size is expressed as a percentage of the available screen area to follow `rules.md`. No new user-facing text or exception path was introduced.
