using System.Collections.Specialized;
using System.Windows.Threading;
using ManagedShell.AppBar;

namespace DeskShell.Services;

/// <summary>
/// While something runs fullscreen (usually a game): turn on Do Not Disturb (Windows Focus) if
/// it wasn't already on, and tell DeskShell to pause its background work. Leaving waits a few
/// seconds, so a quick Alt+Tab out of a game doesn't flip everything back and forth.
/// </summary>
public static class GameMode
{
    static FullScreenHelper? _fullscreen;
    static DispatcherTimer? _leaveDelay;
    static bool _weTurnedOnFocus;

    public static bool Active { get; private set; }

    /// <summary>Raised on the UI thread when game mode starts (true) or ends (false).</summary>
    public static event Action<bool>? Changed;

    public static void Start(FullScreenHelper fullscreen)
    {
        _fullscreen = fullscreen;
        _leaveDelay = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Normal, (_, _) =>
        {
            _leaveDelay!.Stop();
            if (_fullscreen?.FullScreenApps.Count == 0) Set(false);
        }, Dispatcher.CurrentDispatcher);
        _leaveDelay.Stop();
        fullscreen.FullScreenApps.CollectionChanged += OnFullscreenChanged;
    }

    static void OnFullscreenChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (!App.Settings.GameMode) return;
            if (_fullscreen?.FullScreenApps.Count > 0)
            {
                _leaveDelay?.Stop();
                Set(true);
            }
            else if (Active)
            {
                _leaveDelay?.Stop();
                _leaveDelay?.Start();
            }
        });

    static void Set(bool on)
    {
        if (on == Active) return;
        Active = on;
        var app = _fullscreen?.FullScreenApps.FirstOrDefault()?.title;
        Log.Info(on ? $"Game mode on ({app})" : "Game mode off");

        if (on)
        {
            if (App.Settings.GameModeDoNotDisturb && FocusService.IsSupported && !FocusService.IsActive)
            {
                FocusService.SetActive(true);
                _weTurnedOnFocus = true;
            }
        }
        else if (_weTurnedOnFocus)
        {
            // Only switch it off if we were the ones who switched it on.
            if (FocusService.IsActive) FocusService.SetActive(false);
            _weTurnedOnFocus = false;
        }

        Changed?.Invoke(on);
    }

    /// <summary>Ends game mode now (e.g. when DeskShell quits), undoing Do Not Disturb if we set it.</summary>
    public static void Stop()
    {
        if (_fullscreen != null) _fullscreen.FullScreenApps.CollectionChanged -= OnFullscreenChanged;
        _leaveDelay?.Stop();
        Set(false);
    }
}
