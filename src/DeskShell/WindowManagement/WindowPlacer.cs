using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using ManagedShell;
using ManagedShell.WindowsTasks;
using static DeskShell.WindowManagement.WmNative;

namespace DeskShell.WindowManagement;

/// <summary>
/// Places new windows: first by your app rules, otherwise back where that app's window was
/// last time (so layouts survive a reboot). Positions are saved as windows are moved and
/// every minute.
/// </summary>
internal sealed class WindowPlacer : IDisposable
{
    sealed class Saved
    {
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }
        public bool Maximized { get; set; }
    }

    static readonly string StorePath = Path.Combine(Settings.Folder, "window-positions.json");

    readonly ShellManager _shell;
    readonly Dictionary<string, Saved> _saved;
    readonly DispatcherTimer _snapshot;
    readonly DateTime _startedAt = DateTime.Now;
    readonly HashSet<IntPtr> _seen = new();

    public WindowPlacer(ShellManager shell)
    {
        _shell = shell;
        _saved = Load();
        if (shell.Tasks.GroupedWindows?.SourceCollection is INotifyCollectionChanged source)
            source.CollectionChanged += OnWindowsChanged;
        foreach (var w in AllWindows()) _seen.Add(w.Handle);

        _snapshot = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) => SaveAll(), Dispatcher.CurrentDispatcher);
    }

    IEnumerable<ApplicationWindow> AllWindows() =>
        _shell.Tasks.GroupedWindows?.SourceCollection?.OfType<ApplicationWindow>().ToList() ?? new List<ApplicationWindow>();

    void OnWindowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems == null) return;
        // Windows that already existed when DeskShell started are left alone.
        if (DateTime.Now - _startedAt < TimeSpan.FromSeconds(4)) return;

        foreach (var window in e.NewItems.OfType<ApplicationWindow>())
        {
            if (!_seen.Add(window.Handle)) continue;
            var hwnd = window.Handle;
            // Give the app a moment to finish placing its own window first.
            System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(700);
                try { Place(window, hwnd); }
                catch (Exception ex) { Log.Error("Placing a new window failed", ex); }
            });
        }
    }

    void Place(ApplicationWindow window, IntPtr hwnd)
    {
        if (!IsWindow(hwnd) || !window.ShowInTaskbar) return;
        var app = Path.GetFileNameWithoutExtension(window.WinFileName ?? "");

        var rule = App.Settings.AppRules.FirstOrDefault(r =>
            r.App.Length > 0 &&
            string.Equals(Path.GetFileNameWithoutExtension(r.App), app, StringComparison.OrdinalIgnoreCase) &&
            (r.TitleContains.Length == 0 || (window.Title ?? "").Contains(r.TitleContains, StringComparison.OrdinalIgnoreCase)));

        if (rule != null)
        {
            ApplyRule(rule, hwnd);
            Log.Info($"Placed {app} by rule");
            return;
        }

        if (!App.Settings.RestoreWindowPositions) return;
        var key = KeyOf(window);
        // Only restore the first window of an app; extra windows open where the app puts them.
        if (key == null || !_saved.TryGetValue(key, out var saved)) return;
        if (AllWindows().Any(w => w.Handle != hwnd && KeyOf(w) == key)) return;

        var rect = new RECT(saved.Left, saved.Top, saved.Right, saved.Bottom);
        if (!ZoneManager.ScreensLeftToRight().Any(s => s.WorkingArea.IntersectsWith(new System.Drawing.Rectangle(rect.Left, rect.Top, rect.Width, rect.Height))))
            return; // that monitor is gone

        var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref placement)) return;
        placement.rcNormalPosition = rect;
        placement.showCmd = saved.Maximized ? SW_MAXIMIZE : SW_SHOWNORMAL;
        placement.flags = 0;
        SetWindowPlacement(hwnd, ref placement);
    }

    static void ApplyRule(AppRule rule, IntPtr hwnd)
    {
        var screens = ZoneManager.ScreensLeftToRight();
        var screen = rule.Monitor >= 1 && rule.Monitor <= screens.Count ? screens[rule.Monitor - 1] : ZoneManager.ScreenOf(hwnd);

        if (rule.Zone > 0 && ZoneManager.SnapToZone(hwnd, screen, rule.Zone))
        {
            // placed in zone
        }
        else if (rule.Monitor > 0)
        {
            // Centre on the chosen monitor, keeping the window's size.
            if (IsZoomed(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            GetWindowRect(hwnd, out var r);
            var area = screen.WorkingArea;
            var w = Math.Min(r.Width, area.Width);
            var h = Math.Min(r.Height, area.Height);
            var x = area.X + (area.Width - w) / 2;
            var y = area.Y + (area.Height - h) / 2;
            SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
        }

        if (rule.Maximize && rule.Zone == 0) ShowWindow(hwnd, SW_MAXIMIZE);
        if (rule.AlwaysOnTop) SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    static string? KeyOf(ApplicationWindow window)
    {
        if (string.IsNullOrEmpty(window.WinFileName)) return null;
        return $"{window.WinFileName.ToLowerInvariant()}|{window.ClassName}";
    }

    /// <summary>Remembers where a window is now (call after the user moves it).</summary>
    public void Remember(IntPtr hwnd)
    {
        var window = AllWindows().FirstOrDefault(w => w.Handle == hwnd);
        if (window != null && Capture(window)) Save();
    }

    bool Capture(ApplicationWindow window)
    {
        var key = KeyOf(window);
        if (key == null || IsIconic(window.Handle)) return false;
        var p = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(window.Handle, ref p)) return false;
        var r = p.rcNormalPosition;
        if (r.Width < 100 || r.Height < 60) return false;
        _saved[key] = new Saved { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom, Maximized = IsZoomed(window.Handle) };
        return true;
    }

    public void SaveAll()
    {
        foreach (var w in AllWindows().Where(w => w.ShowInTaskbar)) Capture(w);
        Save();
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_saved));
        }
        catch (Exception ex)
        {
            Log.Error("Could not save window positions", ex);
        }
    }

    static Dictionary<string, Saved> Load()
    {
        try
        {
            if (File.Exists(StorePath))
                return JsonSerializer.Deserialize<Dictionary<string, Saved>>(File.ReadAllText(StorePath)) ?? new();
        }
        catch (Exception ex)
        {
            Log.Error("Could not read saved window positions", ex);
        }
        return new();
    }

    public void Dispose()
    {
        _snapshot.Stop();
        if (_shell.Tasks.GroupedWindows?.SourceCollection is INotifyCollectionChanged source)
            source.CollectionChanged -= OnWindowsChanged;
    }
}
