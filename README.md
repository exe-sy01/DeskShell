# DeskShell

A replacement for the Windows taskbar: a slim window dock, a centered shell panel, and a window manager. Fences keeps working alongside it.

## Run it

Built copy: `dist\DeskShell.exe` (needs the .NET 10 desktop runtime). To rebuild it:

```
dotnet publish src/DeskShell -c Release -r win-x64 --self-contained false -o dist
```

Or run from source: `dotnet run --project src/DeskShell`.

**Getting the normal taskbar back:** `Ctrl+Alt+Shift+Q` or the ✕ in the shell. If DeskShell crashes or is killed, a small watchdog process restores the taskbar automatically.

DeskShell never changes the Windows taskbar's settings (such as auto-hide). It only hides the taskbar window and reserves a strip at the bottom of each screen for the dock, and gives both back when it quits.

## Look

Windows 11 Fluent styling to match the [Files](https://files.community) app: follows Windows' light/dark mode and your accent colour, and updates live when you change either.

## File manager

DeskShell doesn't include its own file manager; it's set up alongside [Files](https://files.community), which opens folders, Win+E and "Show in folder" instead of File Explorer. To switch back to File Explorer, turn off "Set Files as default" in Files' settings (Advanced).

## What's in it

**Dock** (bottom of every monitor): the shell button, pinned items, then that monitor's open windows with icons and names. Click a window to switch, click again to minimise, right-click to close. Hides while a fullscreen game runs.

**Pinning to the dock:** right-click an open window → *Pin to dock*, right-click an app in shell search → *Pin to dock*, or drag files, folders or shortcuts onto the dock. A pinned app's windows show on its pin (click to open, switch, or step through its windows; right-click for its window list, *Open new window*, *Move left/right*, *Unpin*).

**Shell** (`Alt+Space`): type to search and launch any app. Tiles below, all movable and resizable with **Customise**:

| Tile | Notes |
|---|---|
| Pinned apps | Right-click a search result → Pin |
| Wi-Fi / network, Bluetooth | Click toggles, right-click opens Windows settings |
| Do not disturb | Uses Windows 11 Focus, which turns on Do Not Disturb |
| Dark mode | Switches Windows light/dark |
| Volume, Sound output | Output switches the default device for everything |
| Brightness | Laptop screens, and external monitors that support DDC/CI |
| Now playing | Whatever Windows sees playing (Spotify, browsers…) |
| CPU / memory / GPU | Updates every second while the shell is open |
| Weather | Set your city in settings (Open-Meteo) |
| Calendar, Screenshot, Window layout | |
| Clipboard history | Last 30 texts you copied, memory only; password managers' copies are skipped |
| Notifications | Read-only copy of Windows' recent notifications |
| Background apps | Your tray icons |

Plus Lock / Sleep / Restart / Shut down (restart and shut down need a second click), and a strip of every open window.

**Games**
- The *Games* tile lists every installed game with box art where the launcher has it: Steam (all libraries), Epic, the Xbox app / Game Pass, and games registered by Battle.net, Ubisoft, EA, Riot and Rockstar. Click to play; games also show up in shell search.
- **Game mode**: while something runs fullscreen, DeskShell turns on Do Not Disturb (Windows Focus, only if it wasn't already on), closes the shell, pauses its background checks and frees memory. It ends 5 seconds after you leave the game. Both parts can be switched off in settings.

**Windows**
- **Alt+Tab**: DeskShell's own switcher with live previews, most recent first. Windows' own Alt+Tab is used while a fullscreen game runs.
- **Show all windows** (`Ctrl+Alt+Up`, or the button in the shell): every window side by side. Click to switch, middle-click or × to close.
- **Snap zones**: hold **Shift** while dragging a window, drop it on a zone. Each monitor has its own layout (pick in settings or with the Window layout tile). Gaps are adjustable.
- **App rules**: e.g. "Discord always opens on monitor 2, zone 1".
- **Reopen apps where they were**: window positions are remembered across restarts.
- **Active window border**: coloured border on the focused window (Windows 11).

## Settings

The gear in the shell opens the settings window. Everything is also in `%APPDATA%\DeskShell\settings.json`. Window positions are kept in `window-positions.json` next to it. Log: `%LOCALAPPDATA%\DeskShell\deskshell.log`.

For bug hunting, create an empty file named `debug` in `%LOCALAPPDATA%\DeskShell` and restart DeskShell: ManagedShell's detailed log then goes to `managedshell.log` there. Delete the file when done (that log grows quickly).

"Start DeskShell when I sign in" is off until you turn it on.

## Known limits

- Notifications can't be dismissed from the tile (Windows doesn't allow it); the button opens Windows' notification center.
- Do Not Disturb has no direct switch in Windows, so the tile starts/stops a Focus session.
- Clipboard history keeps text only, not images.

## Built with

C# / .NET 10 / WPF and [ManagedShell](https://github.com/cairoshell/ManagedShell) (window tracking, tray icons, hiding the Windows taskbar).
