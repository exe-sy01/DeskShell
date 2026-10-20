using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ManagedShell;
using ManagedShell.ShellFolders;
using ManagedShell.WindowsTasks;

namespace DeskShell.Services;

/// <summary>One pinned item as the dock shows it: icon, name, and whether it's running.</summary>
public sealed class DockPinItem : INotifyPropertyChanged
{
    bool _running, _active;

    public DockPinItem(DockPin pin, ImageSource? icon)
    {
        Pin = pin;
        Icon = icon;
    }

    public DockPin Pin { get; }
    public string Name => Pin.Name;
    public ImageSource? Icon { get; }

    /// <summary>This app's open windows (empty for files, folders and closed apps).</summary>
    public List<ApplicationWindow> Windows { get; internal set; } = new();

    public bool IsRunning
    {
        get => _running;
        internal set { if (_running != value) { _running = value; Raise(nameof(IsRunning)); } }
    }

    public bool IsActive
    {
        get => _active;
        internal set { if (_active != value) { _active = value; Raise(nameof(IsActive)); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Apps, files and folders pinned to the dock, shared by the docks on every monitor. A pinned app's
/// windows are shown on its pin (with the running indicator) instead of separately, like the
/// Windows 11 taskbar.
/// </summary>
public static class DockPins
{
    static ShellManager? _shell;
    static AppIndex? _apps;
    static DispatcherTimer? _timer;

    public static ObservableCollection<DockPinItem> Items { get; } = new();

    /// <summary>Raised when pins are added or removed, so docks can re-filter their window lists.</summary>
    public static event Action? Changed;

    public static void Start(ShellManager shell, AppIndex apps)
    {
        _shell = shell;
        _apps = apps;
        foreach (var pin in App.Settings.DockPins) Items.Add(Create(pin));
        Update();
        // Pins of packaged apps saved by program path break when the app updates; once the app
        // list has loaded, switch them to the app's id.
        Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(3000);
            var changed = false;
            for (var i = 0; i < Items.Count; i++)
            {
                var pin = Items[i].Pin;
                if (pin.Kind != "exe" || PackagedAppFor(pin.Target) is not { } app) continue;
                Items[i] = Create(new DockPin { Kind = "app", Target = app.Target, Name = pin.Name });
                changed = true;
            }
            if (changed) Save();
        });
        // Cheap enough to just check a few times a second; only real changes raise property events.
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => Update(), Dispatcher.CurrentDispatcher);
    }

    public static void Stop() => _timer?.Stop();

    /// <summary>Pauses or resumes checking which pinned apps are running (paused during game mode; the dock is hidden then).</summary>
    public static void SetPaused(bool paused)
    {
        if (_timer == null) return;
        if (paused) _timer.Stop();
        else
        {
            Update();
            _timer.Start();
        }
    }

    static IEnumerable<ApplicationWindow> AllWindows() =>
        _shell?.Tasks.GroupedWindows?.SourceCollection?.OfType<ApplicationWindow>().Where(w => w.ShowInTaskbar) ?? [];

    static string _matched = "";

    static void Update()
    {
        var windows = AllWindows().ToList();
        foreach (var item in Items)
        {
            item.Windows = windows.Where(w => Matches(item.Pin, w)).ToList();
            item.IsRunning = item.Windows.Count > 0;
            item.IsActive = item.Windows.Any(w => w.State == ApplicationWindow.WindowState.Active);
        }

        // If a window newly turns out to belong to a pin (e.g. its app id arrived late), docks re-filter.
        var matched = string.Join(",", Items.SelectMany(i => i.Windows).Select(w => w.Handle));
        if (matched != _matched)
        {
            _matched = matched;
            Changed?.Invoke();
        }
    }

    /// <summary>True if this window belongs to a pinned app (so the dock shows it on the pin instead).</summary>
    public static bool IsPinned(ApplicationWindow window) => Items.Any(i => Matches(i.Pin, window));

    static bool Matches(DockPin pin, ApplicationWindow window)
    {
        switch (pin.Kind)
        {
            case "app":
                if (!string.IsNullOrEmpty(window.AppUserModelID) &&
                    string.Equals(window.AppUserModelID, pin.Target, StringComparison.OrdinalIgnoreCase))
                    return true;
                // Store/packaged apps (e.g. Files): match the window's package, whatever version is installed.
                if (pin.Target.Contains('!') && PackageFamilyOf(window.WinFileName) is { } family &&
                    pin.Target.StartsWith(family + "!", StringComparison.OrdinalIgnoreCase))
                    return true;
                // Desktop apps are often listed by their program file, e.g. "{ProgramFiles}\Mozilla Firefox\firefox.exe".
                var exe = ResolveAppPath(pin.Target);
                return exe != null && SamePath(exe, window.WinFileName);
            case "exe":
                return SamePath(pin.Target, window.WinFileName);
            default:
                return false;
        }
    }

    static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>
    /// "C:\Program Files\WindowsApps\Files_4.3.0.0_x64__1y0xx7n9077q4\Files.exe" → "Files_1y0xx7n9077q4"
    /// (the package family, which stays the same across updates). Null for normal programs.
    /// </summary>
    static string? PackageFamilyOf(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        const string marker = "\\WindowsApps\\";
        var start = exePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        var folder = exePath[(start + marker.Length)..].Split('\\')[0];
        var parts = folder.Split('_');
        // name_version_architecture_resourceId_publisherId
        return parts.Length >= 5 ? $"{parts[0]}_{parts[^1]}" : null;
    }

    /// <summary>A packaged app's AppsFolder id for one of its program files, so its pin survives updates.</summary>
    static AppResult? PackagedAppFor(string? exePath) =>
        PackageFamilyOf(exePath) is { } family ? _apps?.FindByPrefix(family + "!") : null;

    static readonly Dictionary<string, string?> ResolvedPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"{known-folder-guid}\rest\app.exe" or a plain path → full path; null for other app ids.</summary>
    static string? ResolveAppPath(string target)
    {
        if (ResolvedPaths.TryGetValue(target, out var cached)) return cached;
        string? result = null;
        try
        {
            if (target.StartsWith('{') && target.IndexOf('}') is var end and > 0 &&
                Guid.TryParse(target[..(end + 1)], out var folderId) &&
                SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out var folder) == 0)
            {
                result = Path.Combine(folder, target[(end + 1)..].TrimStart('\\'));
            }
            else if (target.Contains(":\\") && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                result = target;
            }
        }
        catch
        {
            // not a path-style id
        }
        ResolvedPaths[target] = result;
        return result;
    }

    static DockPinItem Create(DockPin pin)
    {
        ImageSource? icon = null;
        try
        {
            if (pin.Kind == "app") icon = _apps?.IconFor(pin.Target);
            else
            {
                using var shellItem = new ShellItem(pin.Target);
                shellItem.AllowAsync = false;
                icon = shellItem.LargeIcon;
            }
        }
        catch
        {
            // no icon; the dock shows a generic one
        }
        return new DockPinItem(pin, icon);
    }

    public static bool Contains(string kind, string target) =>
        Items.Any(i => i.Pin.Kind == kind && string.Equals(i.Pin.Target, target, StringComparison.OrdinalIgnoreCase));

    public static void Add(DockPin pin)
    {
        if (string.IsNullOrWhiteSpace(pin.Target) || Contains(pin.Kind, pin.Target)) return;
        Items.Add(Create(pin));
        Save();
    }

    /// <summary>Pins the app that owns an open window.</summary>
    public static void PinWindow(ApplicationWindow window)
    {
        if (IsPinned(window)) return;
        var name = window.WinFileDescription;
        if (!string.IsNullOrEmpty(window.AppUserModelID))
        {
            var app = _apps?.Find(window.AppUserModelID);
            Add(new DockPin { Kind = "app", Target = window.AppUserModelID, Name = app?.Name ?? (string.IsNullOrEmpty(name) ? window.Title : name) });
        }
        else if (PackagedAppFor(window.WinFileName) is { } packaged)
        {
            Add(new DockPin { Kind = "app", Target = packaged.Target, Name = packaged.Name });
        }
        else if (!string.IsNullOrEmpty(window.WinFileName))
        {
            Add(new DockPin { Kind = "exe", Target = window.WinFileName, Name = string.IsNullOrEmpty(name) ? Path.GetFileNameWithoutExtension(window.WinFileName) : name });
        }
    }

    /// <summary>Pins files, folders or shortcuts (e.g. dropped on the dock).</summary>
    public static void PinPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var isExe = path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            var name = Directory.Exists(path) && Path.GetPathRoot(path) == path ? path : Path.GetFileNameWithoutExtension(path);
            Add(new DockPin { Kind = isExe ? "exe" : "path", Target = path, Name = name });
        }
    }

    public static void Remove(DockPinItem item)
    {
        Items.Remove(item);
        Save();
    }

    public static void Move(DockPinItem item, int newIndex)
    {
        var old = Items.IndexOf(item);
        if (old < 0 || newIndex < 0 || newIndex >= Items.Count || old == newIndex) return;
        Items.Move(old, newIndex);
        Save();
    }

    static void Save()
    {
        App.Settings.DockPins = Items.Select(i => i.Pin).ToList();
        App.Settings.Save();
        _matched = "\0"; // force docks to re-filter
        Update();
    }

    /// <summary>Click: open it, switch to it, or step through its windows.</summary>
    public static void Activate(DockPinItem item)
    {
        Update();
        var windows = item.Windows;
        if (windows.Count == 0)
        {
            Launch(item.Pin);
            return;
        }

        var active = windows.FindIndex(w => w.State == ApplicationWindow.WindowState.Active);
        if (windows.Count == 1 && active == 0 && windows[0].CanMinimize)
        {
            windows[0].Minimize();
            return;
        }
        // Next window after the active one, or the first.
        var next = windows[(active + 1) % windows.Count];
        WindowManagement.WmNative.Activate(next.Handle);
    }

    public static void Launch(DockPin pin)
    {
        try
        {
            switch (pin.Kind)
            {
                case "app":
                    AppIndex.Launch(pin.Target);
                    break;
                default:
                    Process.Start(new ProcessStartInfo(pin.Target) { UseShellExecute = true });
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open {pin.Name}", ex);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out string path);
}
