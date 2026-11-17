using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using DeskShell.Services;
using DeskShell.WindowManagement;
using ManagedShell;
using ManagedShell.AppBar;
using ManagedShell.Common.Enums;
using Microsoft.Win32;

namespace DeskShell;

public partial class App : Application
{
    public static Settings Settings { get; private set; } = new();
    public static App Instance => (App)Current;

    public ShellManager Shell { get; private set; } = null!;
    public ZoneManager Zones { get; private set; } = null!;
    internal OverviewWindow Overview { get; private set; } = null!;

    readonly List<DockWindow> _docks = new();
    readonly List<IslandWindow> _islands = new();
    readonly List<Notch.NotchWindow> _notches = new();
    AppIndex? _apps;
    AppDrawerWindow? _drawer;
    TaskManagerWindow? _taskManager;

    /// <summary>Now playing, shared by the island and the shell.</summary>
    public MediaService Media { get; } = new();
    ShellWindow? _shellWindow;
    SettingsWindow? _settingsWindow;
    HotkeyManager? _hotkeys;
    TaskbarHider? _hider;
    WorkAreaReserver? _workArea;
    WindowEvents? _events;
    ActiveBorder? _border;
    WindowPlacer? _placer;
    AltTabController? _altTab;
    ClipboardHistory? _clipboard;
    Mutex? _singleInstance;
    SystemWatcher? _watcher;
    DispatcherTimer? _trimTimer;
    bool _quitting;

    /// <summary>Which monitors a fullscreen or borderless window covers; the docks hide there.</summary>
    public static FullscreenWatcher? Fullscreen { get; private set; }

    public bool IsShellOpen => _shellWindow?.IsOpen == true;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, "DeskShell.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }
        // The installer and updater ask a running DeskShell to quit before replacing its files.
        Installer.ListenForQuitRequests(() => Dispatcher.BeginInvoke(Quit));
        // Other programs (a script, a Stream Deck button) can open Home by setting this event.
        var home = new EventWaitHandle(false, EventResetMode.AutoReset, "DeskShell.ToggleHome");
        ThreadPool.RegisterWaitForSingleObject(home, (_, _) => Dispatcher.BeginInvoke(ToggleHome), null, Timeout.Infinite, false);

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Unhandled UI error", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            Log.Error("Fatal error", ex.ExceptionObject as Exception);
            TaskbarGuard.Restore();
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error("Background task failed", ex.Exception);
            ex.SetObserved();
        };

        base.OnStartup(e);
        Log.Info("Starting DeskShell");
        ThemeManager.Start();
        // Tiles are built in code from the current colours; rebuild them when Windows' theme or accent changes.
        ThemeManager.Changed += () => _shellWindow?.RebuildTiles();

        // Creating %LOCALAPPDATA%\DeskShell\debug turns on ManagedShell's detailed log, for bug hunting.
        var logFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskShell");
        if (System.IO.File.Exists(System.IO.Path.Combine(logFolder, "debug")))
        {
            var fileLog = new ManagedShell.Common.Logging.Observers.FileLog(System.IO.Path.Combine(logFolder, "managedshell.log"));
            fileLog.Open();
            ManagedShell.Common.Logging.ShellLogger.Severity = ManagedShell.Common.Logging.LogSeverity.Debug;
            ManagedShell.Common.Logging.ShellLogger.Attach(fileLog, false);
        }
        Settings = Settings.Load();

        var config = ShellManager.DefaultShellConfig;
        config.EnableTasksService = true;
        config.AutoStartTasksService = true;
        config.MultiMonAwareTasksService = true;
        config.TaskIconSize = IconSize.Large;
        config.EnableTrayService = true;
        config.AutoStartTrayService = true;
        Shell = new ShellManager(config);

        TaskbarGuard.Arm();
        _hider = new TaskbarHider();
        _ = FinishStartupAsync();
    }

    /// <summary>
    /// The rest of startup runs once the screen layout has settled after hiding the taskbar,
    /// so the docks don't claim screen space while it is still changing.
    /// </summary>
    async Task FinishStartupAsync()
    {
        if (!Settings.ShowWindowsTaskbar) _hider!.Start();
        await WaitForWorkAreaToSettle();
        if (_quitting) return;
        if (Settings.ShowWindowsTaskbar) HandTrayToWindows();

        var apps = _apps = new AppIndex();
        apps.Start();
        DockPins.Start(Shell, apps);
        Fullscreen = new FullscreenWatcher();
        // After a graphics driver reset WPF can leave the island and docks dead; start afresh instead.
        GraphicsResetWatcher.Reset += () =>
        {
            GraphicsResetWatcher.StartReplacement();
            Dispatcher.BeginInvoke(Quit);
        };
        GraphicsResetWatcher.Start();
        _ = Media.StartAsync();
        NotificationWatcher.Start();
        DiscordLink.Start();
        _clipboard = new ClipboardHistory();
        ScreenTime.Start();
        CreateBars();
        StartWatchingSystem();

        // Window management
        Zones = new ZoneManager();
        var mru = new WindowMru();
        _border = new ActiveBorder();
        _placer = new WindowPlacer(Shell);
        _events = new WindowEvents();
        _events.Foreground += hwnd =>
        {
            Fullscreen.CheckSoon();
            mru.Touch(hwnd);
            _border.OnForeground(hwnd);
        };
        _events.MoveSizeStart += Zones.OnMoveSizeStart;
        _events.MoveSizeEnd += hwnd =>
        {
            Zones.OnMoveSizeEnd(hwnd);
            Dispatcher.BeginInvoke(() => _placer.Remember(hwnd), DispatcherPriority.Background);
        };
        _altTab = new AltTabController(Shell, mru) { CatchWinKey = Settings.WinKeyOpensDrawer, CatchTaskManagerKey = Settings.UseDeskShellTaskManager, CatchExplorerKey = Settings.UseDeskShellFiles };
        _altTab.WinKeyTapped += ToggleAppDrawer;
        _altTab.TaskManagerKey += OpenTaskManager;
        _altTab.ExplorerKey += () => OpenFolder(null);
        _drawer = new AppDrawerWindow(apps);
        Overview = new OverviewWindow(Shell);

        // The shell panel
        _shellWindow = new ShellWindow(Shell, apps, _clipboard, Media);

        BadgeService.Instance.Start(Shell);
        _ = GameLibrary.RefreshAsync();

        // Game mode: while something runs fullscreen, get out of the way and do as little as possible.
        GameMode.Start(Fullscreen);
        GameMode.Changed += on =>
        {
            BadgeService.Instance.SetPaused(on);
            NotificationWatcher.Paused = on;
            DockPins.SetPaused(on);
            _placer?.SetPaused(on);
            if (!on) return;
            HideShell();
            _drawer?.CloseDrawer();
            if (Overview.IsOpen) Overview.CloseOverview();
            TrimMemorySoon(TimeSpan.FromSeconds(2));
        };

        RegisterHotkeys();
        StartupRegistration.Apply(Settings.StartWithWindows);
        ApplyFolderHandler();
        UpdateService.Start();
        // The file explorer's process starts a little later, in the background, so its first window opens at once.
        if (Settings.UseDeskShellFiles)
            _ = Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(20));
                if (!_quitting && Settings.UseDeskShellFiles && !GameMode.Active) Files.FilesHost.Preload();
            });
        TrimMemorySoon(TimeSpan.FromSeconds(15));
    }

    static async Task WaitForWorkAreaToSettle()
    {
        var started = Environment.TickCount64;
        var last = WorkArea();
        var stableSince = Environment.TickCount64;
        while (Environment.TickCount64 - started < 2500)
        {
            await Task.Delay(100);
            var now = WorkArea();
            if (now != last)
            {
                last = now;
                stableSince = Environment.TickCount64;
            }
            else if (Environment.TickCount64 - stableSince >= 400 && Environment.TickCount64 - started >= 300)
            {
                break;
            }
        }
    }

    static (int, int, int, int) WorkArea()
    {
        SystemParametersInfo(0x0030 /* SPI_GETWORKAREA */, 0, out var r, 0);
        return (r.Left, r.Top, r.Right, r.Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    static extern bool SystemParametersInfo(int action, int param, out RECT result, int winIni);

    void RegisterHotkeys()
    {
        _hotkeys?.Dispose();
        _hotkeys = new HotkeyManager();
        if (!_hotkeys.Register(Settings.ShellHotkey, ToggleHome) && Settings.ShellHotkey != "Ctrl+Alt+Space")
            _hotkeys.Register("Ctrl+Alt+Space", ToggleHome);
        _hotkeys.Register(Settings.OverviewHotkey, () => Overview.Toggle());
        _hotkeys.Register(Settings.AppDrawerHotkey, ToggleAppDrawer);
        _hotkeys.Register(Settings.QuitHotkey, Quit);
    }

    /// <summary>Called by the settings window after saving.</summary>
    public void ApplySettings(Settings updated)
    {
        var taskbarChanged = updated.ShowWindowsTaskbar != Settings.ShowWindowsTaskbar;
        var dockChanged = updated.ShowIsland != Settings.ShowIsland || updated.IslandOnAllMonitors != Settings.IslandOnAllMonitors ||
                          updated.IslandReserveSpace != Settings.IslandReserveSpace || updated.IslandLook != Settings.IslandLook ||
                          System.Text.Json.JsonSerializer.Serialize(updated.Notch) != System.Text.Json.JsonSerializer.Serialize(Settings.Notch) ||
                          System.Text.Json.JsonSerializer.Serialize(updated.Island) != System.Text.Json.JsonSerializer.Serialize(Settings.Island) ||
                          updated.ShowDock != Settings.ShowDock || updated.DockOnAllMonitors != Settings.DockOnAllMonitors || Math.Abs(updated.DockHeight - Settings.DockHeight) > 0.5;
        if (!updated.ActiveWindowBorder) _border?.Reset();
        Settings = updated;
        Settings.Save();

        RegisterHotkeys();
        if (_altTab != null)
        {
            _altTab.Enabled = Settings.CustomAltTab;
            _altTab.CatchWinKey = Settings.WinKeyOpensDrawer;
            _altTab.CatchTaskManagerKey = Settings.UseDeskShellTaskManager;
            _altTab.CatchExplorerKey = Settings.UseDeskShellFiles;
        }
        StartupRegistration.Apply(Settings.StartWithWindows);
        ApplyFolderHandler();
        _shellWindow?.UpdateHint();
        if (taskbarChanged) ApplyWindowsTaskbar();
        if (dockChanged) RebuildBars("settings changed");
        else if (taskbarChanged) ReserveSpace();
    }

    public void SetWindowsTaskbar(bool show)
    {
        var updated = Settings.Clone();
        updated.ShowWindowsTaskbar = show;
        ApplySettings(updated);
    }

    void ApplyWindowsTaskbar()
    {
        Log.Info(Settings.ShowWindowsTaskbar ? "Showing the Windows taskbar" : "Hiding the Windows taskbar");
        if (Settings.ShowWindowsTaskbar)
        {
            _hider?.Stop();
            HandTrayToWindows();
        }
        else
        {
            _hider?.Start();
            CallTrayService("Resume");
            AskTrayAppsToReAddIcons();
        }
    }

    /// <summary>Tray apps talk to whichever tray is in front; ours steps back so their icons appear on the Windows taskbar.</summary>
    void HandTrayToWindows()
    {
        CallTrayService("Suspend");
        AskTrayAppsToReAddIcons();
    }

    /// <summary>ManagedShell keeps these internal, so call them by name, like SendTaskbarCreated below.</summary>
    void CallTrayService(string name)
    {
        try
        {
            var method = Shell.TrayService.GetType().GetMethod(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (method == null) Log.Error($"ManagedShell's tray has no {name} any more");
            else method.Invoke(Shell.TrayService, null);
        }
        catch (Exception ex)
        {
            Log.Error($"ManagedShell's tray failed to {name}", ex);
        }
    }

    public void ResetTiles() => _shellWindow?.ResetTiles();

    public void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow();
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>Creates the island(s) and dock(s) the settings ask for and reserves their screen space.</summary>
    void CreateBars()
    {
        _workArea ??= new WorkAreaReserver();
        var all = AppBarScreen.FromAllScreens();
        var islandScreens = IslandScreens(all);
        var dockScreens = DockScreens(all);

        foreach (var screen in islandScreens)
        {
            if (Settings.UseNotch)
            {
                var notch = new Notch.NotchWindow(screen, Media, _clipboard!, _apps!);
                notch.Show();
                _notches.Add(notch);
                continue;
            }
            var island = new IslandWindow(Shell, screen, Media, _apps!);
            island.Show();
            _islands.Add(island);
        }
        foreach (var screen in dockScreens)
        {
            var dock = new DockWindow(Shell, screen, Settings.DockHeight);
            dock.Show();
            _docks.Add(dock);
        }

        ReserveSpace();
    }

    List<AppBarScreen> IslandScreens(List<AppBarScreen> all) => !Settings.ShowIsland ? new List<AppBarScreen>()
        : Settings.IslandOnAllMonitors ? all : all.Where(s => s.Primary).Take(1).ToList();

    List<AppBarScreen> DockScreens(List<AppBarScreen> all) => !Settings.ShowDock ? new List<AppBarScreen>()
        : Settings.DockOnAllMonitors ? all : all.Where(s => s.Primary).Take(1).ToList();

    /// <summary>What the dock or the Windows taskbar covers at the bottom of a screen, in device-independent pixels.</summary>
    public double BottomStrip(AppBarScreen screen)
    {
        var dock = DockScreens(AppBarScreen.FromAllScreens()).Any(s => s.HMonitor == screen.HMonitor) ? Settings.DockHeight : 0.0;
        if (!Settings.ShowWindowsTaskbar) return dock;
        return Math.Max(dock, TaskbarHider.BottomStrip(screen.Bounds) / WindowManagement.WmNative.ScaleFor(screen.HMonitor));
    }

    void ReserveSpace()
    {
        var all = AppBarScreen.FromAllScreens();
        var islandScreens = IslandScreens(all);

        // Every screen gets its usable area set, so the hidden taskbar's strip goes to windows even where there's no bar.
        _workArea?.Reserve(all.Select(screen => (screen,
            Settings.IslandReserveSpace && islandScreens.Any(s => s.HMonitor == screen.HMonitor) ? (Settings.UseNotch ? 36 : Settings.Island.ReservedHeight) : 0,
            BottomStrip(screen))));
    }

    void CloseBars()
    {
        foreach (var dock in _docks)
        {
            dock.AllowClose = true;
            dock.Close();
        }
        _docks.Clear();
        foreach (var island in _islands) island.ForceClose();
        _islands.Clear();
        foreach (var notch in _notches) notch.ForceClose();
        _notches.Clear();
    }

    /// <summary>
    /// Tray apps register their icons with whichever tray answers first; after Explorer restarts that
    /// can be Explorer's. ManagedShell can ask them all to register again (with its tray, which it
    /// keeps in front); that method isn't public, so call it by name, else broadcast the request ourselves.
    /// </summary>
    void AskTrayAppsToReAddIcons()
    {
        try
        {
            var method = Shell.TrayService.GetType().GetMethod("SendTaskbarCreated",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (method != null)
            {
                method.Invoke(Shell.TrayService, null);
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Error("ManagedShell couldn't ask tray apps to re-add icons", ex);
        }
        PostMessage(new IntPtr(0xffff), RegisterWindowMessage("TaskbarCreated"), IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Recreates the island and docks for the current monitors and re-reserves their screen space.</summary>
    public void RebuildBars(string why)
    {
        if (_quitting) return;
        Log.Info($"Rebuilding island and docks ({why})");
        HideShell();
        CloseBars();
        CreateBars();
    }

    void StartWatchingSystem()
    {
        _watcher = new SystemWatcher();
        _watcher.DisplaysChanged += () => RebuildBars("displays changed");
        _watcher.Resumed += () => RebuildBars("woke from sleep");
        _watcher.ExplorerRestarted += () =>
        {
            // Explorer's new taskbar is hidden by TaskbarHider within a moment; Explorer also reset the
            // usable screen area and tray apps re-registered their icons with it.
            RebuildBars("Explorer restarted");
            var before = Shell.NotificationArea.TrayIcons.Count;
            AskTrayAppsToReAddIcons();
            BadgeService.Instance.AnnounceAllButtons(Shell); // and re-send their badges
            Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(4000);
                Log.Info($"After Explorer restart: {Shell.NotificationArea.TrayIcons.Count} tray icons (had {before})");
                if (Settings.ShowWindowsTaskbar) ReserveSpace(); // its new taskbar exists by now
            });
        };
    }

    /// <summary>The shell hotkey: with the notch, its Home panel on the screen you're on; otherwise the shell.</summary>
    public void ToggleHome()
    {
        if (_notches.Count == 0)
        {
            ToggleShell();
            return;
        }
        var screen = WindowManagement.ZoneManager.ScreenAtCursor();
        (_notches.FirstOrDefault(n => n.Screen.HMonitor == screen.HMonitor) ?? _notches[0]).Toggle();
    }

    public void ToggleShell()
    {
        // From the hotkey: grow out of the island on the screen you're on, if there is one.
        var screen = WindowManagement.ZoneManager.ScreenAtCursor();
        OpenShellFrom(_islands.FirstOrDefault(i => i.Screen.HMonitor == screen.HMonitor));
    }

    /// <summary>Opens the shell out of <paramref name="island"/> (or plainly if null), or closes it if it's open.</summary>
    public void OpenShellFrom(IslandWindow? island)
    {
        if (_shellWindow == null) return;
        if (_shellWindow.IsOpen) _shellWindow.CloseShell();
        else
        {
            if (Overview.IsOpen) Overview.CloseOverview();
            _shellWindow.OpenShell(island);
        }
    }

    public void HideShell() => _shellWindow?.CloseShell(immediate: true);

    /// <summary>Opens the app drawer on the screen you're on, or closes it.</summary>
    /// <summary>DeskShell's Task Manager, or Windows' if the settings say so.</summary>
    public void OpenTaskManager()
    {
        if (!Settings.UseDeskShellTaskManager)
        {
            OpenWindowsTaskManager();
            return;
        }
        HideShell();
        _drawer?.CloseDrawer();
        if (_taskManager == null)
        {
            _taskManager = new TaskManagerWindow();
            _taskManager.Closed += (_, _) =>
            {
                _taskManager = null;
                TrimMemorySoon(TimeSpan.FromSeconds(5));
            };
            _taskManager.Show();
        }
        if (_taskManager.WindowState == WindowState.Minimized) _taskManager.WindowState = WindowState.Normal;
        WindowManagement.WmNative.Activate(new System.Windows.Interop.WindowInteropHelper(_taskManager).Handle);
    }

    /// <summary>
    /// Opens a folder (Home if null) in DeskShell's file explorer, or Windows' if the settings say so.
    /// <paramref name="select"/> picks a file in it.
    /// </summary>
    public void OpenFolder(string? path, string? select = null)
    {
        HideShell();
        _drawer?.CloseDrawer();
        if (Settings.UseDeskShellFiles)
        {
            Files.FilesHost.Open(path, select, newWindow: path == null && select == null);
            return;
        }
        var explorer = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        ToolsMenu.Run(explorer, args: select != null ? $"/select,\"{select}\"" : path != null ? $"\"{path}\"" : null);
    }

    /// <summary>Windows sends every folder to DeskShell's explorer only while both settings are on.</summary>
    void ApplyFolderHandler()
    {
        var on = Settings.UseDeskShellFiles && Settings.OpenAllFoldersInDeskShell;
        if (on == Files.FolderHandler.IsOurs()) return;
        Files.FolderHandler.Apply(on, Installer.IsInstalledCopy || !Installer.IsInstalled ? Environment.ProcessPath! : Installer.InstalledExe);
    }

    public static void OpenWindowsTaskManager() => ToolsMenu.Run(System.IO.Path.Combine(Environment.SystemDirectory, "taskmgr.exe"));

    public void ToggleAppDrawer()
    {
        if (_drawer == null) return;
        if (_drawer.IsOpen)
        {
            _drawer.CloseDrawer();
            return;
        }
        HideShell();
        if (Overview.IsOpen) Overview.CloseOverview();
        _drawer.Open(WindowManagement.ZoneManager.ScreenAtCursor());
    }

    /// <summary>
    /// Hands memory DeskShell isn't using back to Windows a little after the shell closes,
    /// so Task Manager shows what it really needs while idle.
    /// </summary>
    public void TrimMemorySoon(TimeSpan? delay = null)
    {
        _trimTimer ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
        _trimTimer.Stop();
        _trimTimer.Interval = delay ?? TimeSpan.FromSeconds(4);
        _trimTimer.Tick -= TrimTick;
        _trimTimer.Tick += TrimTick;
        _trimTimer.Start();
    }

    void TrimTick(object? sender, EventArgs e)
    {
        _trimTimer!.Stop();
        if (IsShellOpen) return;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
    }

    [DllImport("kernel32.dll")]
    static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);

    public void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        Log.Info("Quitting DeskShell");
        // If something hangs while shutting down, leave anyway (with the taskbar back) rather than linger half-closed.
        new Thread(() =>
        {
            Thread.Sleep(8000);
            Log.Error("Quitting took too long; exiting");
            TaskbarGuard.Restore();
            Environment.Exit(0);
        }) { IsBackground = true, Name = "DeskShell quit timeout" }.Start();
        _taskManager?.Close();

        try
        {
            ScreenTime.Save();
            _watcher?.Dispose();
            NotificationWatcher.Stop();
            DiscordLink.Stop();
            Fullscreen?.Dispose();
            UpdateService.Stop();
            _hotkeys?.Dispose();
            GameMode.Stop();
            VoicemeeterService.Disconnect();
            BadgeService.Instance.Stop();
            DockPins.Stop();
            _altTab?.Dispose();
            _events?.Dispose();
            _border?.Reset();
            _placer?.SaveAll();
            _placer?.Dispose();
            Zones?.Dispose();
            _clipboard?.Dispose();
            Overview?.Close();
            _settingsWindow?.Close();
            _drawer?.ForceClose();
            _shellWindow?.ForceClose();
            CloseBars();
            Shell.AppBarManager.SignalGracefulShutdown();
            _hider?.Stop();
            // After Explorer's taskbar is back, so Explorer reclaims its space when it recalculates.
            _workArea?.Release();
            _workArea?.Dispose();
            Shell.Dispose();
            TaskbarGuard.Disarm();
        }
        catch (Exception ex)
        {
            Log.Error("Error while quitting; restoring taskbar directly", ex);
            TaskbarGuard.Restore();
        }

        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        Quit();
        base.OnSessionEnding(e);
    }
}

/// <summary>Adds or removes DeskShell from the programs Windows starts when you sign in.</summary>
internal static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue("DeskShell", $"\"{Environment.ProcessPath}\"");
            else if (key.GetValue("DeskShell") != null) key.DeleteValue("DeskShell");
        }
        catch (Exception ex)
        {
            Log.Error("Could not update start-with-Windows", ex);
        }
    }
}
