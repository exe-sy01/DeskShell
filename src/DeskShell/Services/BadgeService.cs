using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;

namespace DeskShell.Services;

/// <summary>
/// Store apps' unread badges (the number Windows would show on their taskbar button), checked
/// every 15 seconds. Desktop apps such as Discord draw their own badge as an "overlay icon",
/// which ManagedShell already hands us on each window, so those need nothing from here.
/// </summary>
public sealed class BadgeService : INotifyPropertyChanged
{
    public static BadgeService Instance { get; } = new();

    Dictionary<string, string> _badges = new(StringComparer.OrdinalIgnoreCase);
    DispatcherTimer? _timer;
    bool _busy;

    /// <summary>Bumped whenever badges change, so bindings re-read them.</summary>
    public int Version { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Start(ManagedShell.ShellManager shell)
    {
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, async (_, _) => await RefreshAsync(), Dispatcher.CurrentDispatcher);
        _ = RefreshAsync();

        // Apps send their own badge (overlay icon) when Windows tells them their taskbar button
        // exists. Explorer does that; ManagedShell doesn't, so we do: once for every window now,
        // then for each new window.
        var windows = shell.Tasks.GroupedWindows?.SourceCollection;
        if (windows != null)
            foreach (var w in windows.OfType<ManagedShell.WindowsTasks.ApplicationWindow>()) AnnounceButton(w.Handle);
        if (windows is System.Collections.Specialized.INotifyCollectionChanged changes)
            changes.CollectionChanged += (_, e) =>
            {
                if (e.NewItems == null) return;
                foreach (var w in e.NewItems.OfType<ManagedShell.WindowsTasks.ApplicationWindow>())
                {
                    _ = Application.Current.Dispatcher.InvokeAsync(async () =>
                    {
                        await Task.Delay(500);
                        AnnounceButton(w.Handle);
                        await Task.Delay(1000);
                        // A new window's app id is known by now; let its badge show.
                        Bump();
                    });
                }
            };
    }

    /// <summary>Asks every open window to re-send its badge (e.g. after Explorer restarted).</summary>
    public void AnnounceAllButtons(ManagedShell.ShellManager shell)
    {
        foreach (var w in shell.Tasks.GroupedWindows?.SourceCollection?.OfType<ManagedShell.WindowsTasks.ApplicationWindow>() ?? [])
            AnnounceButton(w.Handle);
    }

    static readonly uint TaskbarButtonCreated = RegisterWindowMessage("TaskbarButtonCreated");

    static void AnnounceButton(IntPtr hwnd) => PostMessage(hwnd, TaskbarButtonCreated, IntPtr.Zero, IntPtr.Zero);

    void Bump()
    {
        Version++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Version)));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern uint RegisterWindowMessage(string message);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    public void Stop() => _timer?.Stop();

    /// <summary>Pauses or resumes the 15-second check (paused during game mode).</summary>
    public void SetPaused(bool paused)
    {
        if (_timer == null) return;
        if (paused) _timer.Stop();
        else
        {
            _timer.Start();
            _ = RefreshAsync();
        }
    }

    public async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var fresh = await NotificationReader.ReadBadgesAsync();
            if (fresh.Count == _badges.Count && fresh.All(kv => _badges.TryGetValue(kv.Key, out var v) && v == kv.Value)) return;
            _badges = fresh;
            Bump();
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>The badge for an app id, matching on the app's package if the exact id differs.</summary>
    public string? For(string? appId)
    {
        if (string.IsNullOrEmpty(appId) || _badges.Count == 0) return null;
        if (_badges.TryGetValue(appId, out var exact)) return exact;
        var package = appId.Split('!')[0];
        if (!appId.Contains('!')) return null; // desktop app ids have no package part
        return _badges.FirstOrDefault(kv => kv.Key.Split('!')[0].Equals(package, StringComparison.OrdinalIgnoreCase)).Value;
    }
}

/// <summary>
/// Turns (app id, badge version) into the badge text: "3", "99+", or "•" for symbol badges.
/// With ConverterParameter "visibility" it returns whether to show the badge at all.
/// </summary>
public sealed class BadgeConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var raw = BadgeService.Instance.For(values.Length > 0 ? values[0] as string : null);
        var text = raw == null ? "" : int.TryParse(raw, out var n) ? (n > 99 ? "99+" : n.ToString(culture)) : "•";
        if (parameter as string == "visibility") return text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        return text;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
