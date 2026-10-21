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
        _hider!.Start();
        await WaitForWorkAreaToSettle();
        if (_quitting) return;

        var apps = new AppIndex();
        apps.Start();
        DockPins.Start(Shell, apps);
        Fullscreen = new FullscreenWatcher();
        CreateDocks();
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
        _altTab = new AltTabController(Shell, mru);
        Overview = new OverviewWindow(Shell);

        // The shell panel
        _clipboard = new ClipboardHistory();
        _shellWindow = new ShellWindow(Shell, apps, _clipboard);

        BadgeService.Instance.Start(Shell);
        _ = GameLibrary.RefreshAsync();

        // Game mode: while something runs fullscreen, get out of the way and do as little as possible.
        GameMode.Start(Shell.FullScreenHelper);
        GameMode.Changed += on =>
        {
            BadgeService.Instance.SetPaused(on);
            DockPins.SetPaused(on);
            _placer?.SetPaused(on);
            if (!on) return;
            HideShell();
            if (Overview.IsOpen) Overview.CloseOverview();
            TrimMemorySoon(TimeSpan.FromSeconds(2));
        };

        RegisterHotkeys();
        StartupRegistration.Apply(Settings.StartWithWindows);
        UpdateService.Start();
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
        if (!_hotkeys.Register(Settings.ShellHotkey, ToggleShell) && Settings.ShellHotkey != "Ctrl+Alt+Space")
            _hotkeys.Register("Ctrl+Alt+Space", ToggleShell);
        _hotkeys.Register(Settings.OverviewHotkey, () => Overview.Toggle());
        _hotkeys.Register(Settings.QuitHotkey, Quit);
    }

    /// <summary>Called by the settings window after saving.</summary>
    public void ApplySettings(Settings updated)
    {
        var dockChanged = updated.ShowDock != Settings.ShowDock || updated.DockOnAllMonitors != Settings.DockOnAllMonitors || Math.Abs(updated.DockHeight - Settings.DockHeight) > 0.5;
        if (!updated.ActiveWindowBorder) _border?.Reset();
        Settings = updated;
        Settings.Save();

        RegisterHotkeys();
        if (_altTab != null) _altTab.Enabled = Settings.CustomAltTab;
        StartupRegistration.Apply(Settings.StartWithWindows);
        _shellWindow?.UpdateHint();
        if (dockChanged)
        {
            CloseDocks();
            CreateDocks();
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

    void CreateDocks()
    {
        _workArea ??= new WorkAreaReserver();
        if (!Settings.ShowDock)
        {
            // No dock: still keep the hidden taskbar's strip free for windows on every screen.
            _workArea.Reserve(AppBarScreen.FromAllScreens(), 0);
            return;
        }

        var screens = Settings.DockOnAllMonitors
            ? AppBarScreen.FromAllScreens()
            : new List<AppBarScreen> { AppBarScreen.FromPrimaryScreen() };

        foreach (var screen in screens)
        {
            var dock = new DockWindow(Shell, screen, Settings.DockHeight);
            dock.Show();
            _docks.Add(dock);
        }

        _workArea.Reserve(screens, Settings.DockHeight);
    }

    void CloseDocks()
    {
        foreach (var dock in _docks)
        {
            dock.AllowClose = true;
            dock.Close();
        }
        _docks.Clear();
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

    /// <summary>Recreates the docks for the current monitors and re-reserves their screen space.</summary>
    void RebuildDocks(string why)
    {
        if (_quitting) return;
        Log.Info($"Rebuilding docks ({why})");
        HideShell();
        CloseDocks();
        CreateDocks();
    }

    void StartWatchingSystem()
    {
        _watcher = new SystemWatcher();
        _watcher.DisplaysChanged += () => RebuildDocks("displays changed");
        _watcher.Resumed += () => RebuildDocks("woke from sleep");
        _watcher.ExplorerRestarted += () =>
        {
            // Explorer's new taskbar is hidden by TaskbarHider within a moment; Explorer also reset the
            // usable screen area and tray apps re-registered their icons with it.
            RebuildDocks("Explorer restarted");
            var before = Shell.NotificationArea.TrayIcons.Count;
            AskTrayAppsToReAddIcons();
            BadgeService.Instance.AnnounceAllButtons(Shell); // and re-send their badges
            Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(4000);
                Log.Info($"After Explorer restart: {Shell.NotificationArea.TrayIcons.Count} tray icons (had {before})");
            });
        };
    }

    public void ToggleShell()
    {
        if (_shellWindow == null) return;
        if (_shellWindow.IsOpen) _shellWindow.CloseShell();
        else
        {
            if (Overview.IsOpen) Overview.CloseOverview();
            _shellWindow.OpenShell();
        }
    }

    public void HideShell() => _shellWindow?.CloseShell(immediate: true);

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

        try
        {
            _watcher?.Dispose();
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
            _shellWindow?.ForceClose();
            CloseDocks();
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
