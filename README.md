# DeskShell

A replacement for the Windows taskbar: a slim window dock, a centered shell panel, and a window manager. Fences keeps working alongside it.

## Install it

Needs the .NET 10 desktop runtime. Build it, then install:

```
.\build-release.ps1
dist\DeskShell.exe --install
```

This copies DeskShell to `%LOCALAPPDATA%\Programs\DeskShell` (no admin needed) and adds it to the Start menu and to Settings → Apps → Installed apps, where you can uninstall it. Settings → *Install DeskShell* does the same from a running copy. Uninstalling keeps your settings in `%APPDATA%\DeskShell`.

Or run from source without installing: `dotnet run --project src/DeskShell`.

**Updates:** put a GitHub repository (`owner/name`) in Settings → *Update from GitHub repo*. DeskShell checks its latest release once a day; when a release tagged with a newer version (e.g. `v1.1.0`) has a `.zip` attached, an *Update* button appears in the shell. It downloads the zip, quits DeskShell, installs the new version and starts it. To publish one: bump `<Version>` in `src/DeskShell/DeskShell.csproj`, run `build-release.ps1`, and attach `release\DeskShell-<version>.zip` to a release tagged `v<version>`. With no repository set, nothing is checked.

**Getting the normal taskbar back:** `Ctrl+Alt+Shift+Q` or the ✕ in the shell. If DeskShell crashes or is killed, a small watchdog process restores the taskbar automatically.

DeskShell never changes the Windows taskbar's settings (such as auto-hide). It only hides the taskbar window and reserves a strip at the bottom of each screen for the dock, and gives both back when it quits.

## Look

Windows 11 Fluent styling to match the [Files](https://files.community) app: follows Windows' light/dark mode and your accent colour, and updates live when you change either.

## File manager

DeskShell doesn't include its own file manager; it's set up alongside [Files](https://files.community), which opens folders, Win+E and "Show in folder" instead of File Explorer. To switch back to File Explorer, turn off "Set Files as default" in Files' settings (Advanced).

## What's in it

**Island** (top of the main monitor): a black pill, like Apple's Dynamic Island, that never grows past its size. **‹ ›** or the mouse wheel flip between four pages:

- **Notifications**: the latest one and how many there are, including unread Discord DMs and direct mentions (from the AIChat Vencord plugin). Click a Discord message to open it in Discord, anything else for Windows' notification centre
- **Music**: what's playing, with previous / play-pause / next
- **Open windows**: their icons; click to switch, click again to minimise, right-click to pin, minimise or close
- **Programs**: your pinned apps, files and folders; drag files onto the island to pin them
- **Voice call** (while you're in a Discord call): the channel and everyone in it; whoever's speaking glows. Sent by the AIChat Vencord plugin (`deskshell.ts`) to DeskShell on `127.0.0.1:47390`, which only accepts Discord's own pages.

**Double-click the island** and it grows into the shell (and shrinks back into it when the shell closes). It hides while a fullscreen or borderless window (a game, a video) fills its monitor, and maximised windows sit below it. Settings → Island can put one on every monitor or stop reserving the space. Its size, look, pages, peek mode, notification moments and the shell's animation are designed in Island Workshop (an artifact): press *Copy settings* there, then *Paste from Island Workshop* in DeskShell's settings (or run `DeskShell.exe --apply-island <file.json>`).

The old **dock** at the bottom is still there if you want it (Settings → Dock); it's off by default now.

**App drawer** (tap the Windows key, the all-apps button on the island, *All apps* in the shell, or `Alt+Shift+Space`): DeskShell's Start menu. Search, pinned, recent and every app A-Z, plus Files, Downloads, Documents, Settings and power buttons. Works by keyboard alone: type to search, arrows to move, Enter to open, Esc to close. Windows-key shortcuts such as Win+E still work, and during games the key goes back to Windows. Settings > General turns the Windows-key part off.

**Shell** (double-click the island, or `Alt+Space`): type to search and launch any app. Tiles below, all movable and resizable with **Customise**:

| Tile | Notes |
|---|---|
| Pinned apps | Right-click a search result → Pin |
| Wi-Fi / network, Bluetooth | Click toggles, right-click opens Windows settings |
| Do not disturb | Windows' own Do Not Disturb (the same switch as in Settings → Notifications) |
| Dark mode | Switches Windows light/dark |
| Volume, Sound output | Output switches the default device for everything |
| Mixer | One row per output: Voicemeeter's VAIO, AUX and VAIO3 (their Voicemeeter faders) and any other device apps play through (its Windows volume). Right-click an output to show its apps, each with its own volume. Below: Voicemeeter's A/B outputs |
| Brightness | Laptop screens, and external monitors that support DDC/CI |
| Now playing | Whatever Windows sees playing (Spotify, browsers…) |
| CPU / memory / GPU | Updates every second while the shell is open |
| Weather | Set your city in settings (Open-Meteo) |
| Calendar, Screenshot, Window layout | |
| Clipboard history | Last 30 texts you copied, memory only; password managers' copies are skipped |
| Notifications | Read-only copy of Windows' recent notifications |
| Background apps | Your tray icons |

Plus Lock / Sleep / Restart / Shut down (restart and shut down need a second click).

**Open windows** sit either side of the shell: Monitor 1's windows in a panel on its left, Monitor 2's on its right (underneath the shell on narrow screens). Windows are grouped by app, each as a preview card with its full title. The window you were using is outlined. Click to switch, hover for ×, middle-click to close, right-click for Pin to dock / Minimise / Close.

**Games**
- The *Games* tile lists every installed game with box art where the launcher has it: Steam (all libraries), Epic, the Xbox app / Game Pass, and games registered by Battle.net, Ubisoft, EA, Riot and Rockstar. Click to play; games also show up in shell search.
- **Game mode**: while something runs fullscreen, DeskShell turns on Do Not Disturb (only if it wasn't already on, and switches it off again afterwards), closes the shell, pauses its background checks and frees memory. It ends 5 seconds after you leave the game. Both parts can be switched off in settings.

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

**Recovery:** if Explorer restarts, a monitor is plugged in or changes resolution, or the PC wakes from sleep, DeskShell rebuilds its docks, hides the taskbar again and asks tray apps to re-add their icons.

## Known limits

- Notifications can't be dismissed from the tile (Windows doesn't allow it); the button opens Windows' notification center.
- Clipboard history keeps text only, not images.

## Built with

C# / .NET 10 / WPF and [ManagedShell](https://github.com/cairoshell/ManagedShell) (window tracking, tray icons, hiding the Windows taskbar).
