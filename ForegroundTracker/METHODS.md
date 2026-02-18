# Window Foreground Detection Methods

| # | Method |
|---|--------|
| 1 | `SetWinEventHook` (EVENT_SYSTEM_FOREGROUND) |
| 2 | `SetWinEventHook` (EVENT_OBJECT_FOCUS) |
| 3 | `WndProc` (WM_ACTIVATE / WM_ACTIVATEAPP) |
| 4 | `RegisterShellHookWindow` |
| 5 | `SetWindowsHookEx` (WH_CBT) |
| 6 | UI Automation `FocusChanged` |
| 7 | Polling `GetForegroundWindow` |
| 8 | `Window.Activated` / `Deactivated` |
| 9 | `GotKeyboardFocus` / `LostKeyboardFocus` |
| 10 | `App.Activated` / `Deactivated` |
| 11 | COM UI Automation `FocusChanged` (`IUIAutomationFocusChangedEventHandler`) |
