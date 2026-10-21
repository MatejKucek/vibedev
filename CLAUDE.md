# Vibedev notes

## Colors: never light text on a light background
The UI is dark (see `App.xaml`: Base `#18181B`, Surface `#202024`, popups `#24242A`, border `#605272`, text `#E4E4E7`). Light-on-light / white-on-white has shipped three times already. Before finishing any UI work:

- WPF implicit styles in `App.xaml` match the **exact** type only. They do NOT apply to subclasses. A `Window` subclass (dialogs), AvalonEdit `CompletionWindow`, and `CompletionListBox` (a `ListBox` subclass) all render with the default white background. Set `Background`, `Foreground` (and `FontFamily`/`FontSize`) explicitly on every new `Window` subclass, popup or third-party control.
- App text styles use light foregrounds (`#E4E4E7`, `#D4D4DC`, `#DDD6EE`). Any container they sit in must have an explicit dark background.
- New popup/dialog/list: confirm background AND text colors, including hover and selected states, and ComboBox dropdown items. Reuse the app brushes above, no system colors.
- Verify visually (screenshot or run the app) before reporting done; a passing build does not prove colors are readable.
