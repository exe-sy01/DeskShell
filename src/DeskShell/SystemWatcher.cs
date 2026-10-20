using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DeskShell;

/// <summary>
/// Notices the system changes DeskShell has to recover from: Explorer restarting (it re-creates its
/// taskbar and asks apps to re-add tray icons), monitors or resolutions changing, and waking from
/// sleep. Each is reported once things have settled, on the UI thread.
///
/// Uses its own hidden window rather than SystemEvents, whose handlers run synchronously on the UI
/// thread and once deadlocked DeskShell.
/// </summary>
internal sealed class SystemWatcher : IDisposable
{
    const int WM_DISPLAYCHANGE = 0x007E;
    const int WM_POWERBROADCAST = 0x0218;
    const int PBT_APMRESUMESUSPEND = 0x0007, PBT_APMRESUMEAUTOMATIC = 0x0012;

    readonly HwndSource _window;
    readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    readonly DispatcherTimer _displayDelay, _resumeDelay, _explorerDelay;

    /// <summary>Monitors were added, removed, rearranged, or changed resolution or scale.</summary>
    public event Action? DisplaysChanged;
    /// <summary>The PC woke from sleep or hibernation.</summary>
    public event Action? Resumed;
    /// <summary>Explorer restarted (its taskbar is back and tray apps re-registered with it).</summary>
    public event Action? ExplorerRestarted;

    public SystemWatcher()
    {
        // A normal (not message-only) hidden window, because only those receive broadcasts.
        _window = new HwndSource(new HwndSourceParameters("DeskShellSystemWatcher") { Width = 0, Height = 0, WindowStyle = 0 });
        _window.AddHook(WndProc);

        var dispatcher = Dispatcher.CurrentDispatcher;
        _displayDelay = Delay(TimeSpan.FromSeconds(1.5), () => DisplaysChanged?.Invoke(), dispatcher);
        _resumeDelay = Delay(TimeSpan.FromSeconds(3), () => Resumed?.Invoke(), dispatcher);
        _explorerDelay = Delay(TimeSpan.FromSeconds(2.5), () => ExplorerRestarted?.Invoke(), dispatcher);
    }

    static DispatcherTimer Delay(TimeSpan wait, Action action, Dispatcher dispatcher)
    {
        DispatcherTimer? timer = null;
        timer = new DispatcherTimer(wait, DispatcherPriority.Normal, (_, _) =>
        {
            timer!.Stop();
            try { action(); }
            catch (Exception ex) { Log.Error("Recovering from a system change failed", ex); }
        }, dispatcher);
        timer.Stop();
        return timer;
    }

    static void Restart(DispatcherTimer timer)
    {
        // Several messages usually arrive in a burst; act once, after the last.
        timer.Stop();
        timer.Start();
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DISPLAYCHANGE)
        {
            Log.Info("Display setup changed");
            Restart(_displayDelay);
        }
        else if (msg == WM_POWERBROADCAST && wParam.ToInt32() is PBT_APMRESUMEAUTOMATIC or PBT_APMRESUMESUSPEND)
        {
            Log.Info("Woke from sleep");
            Restart(_resumeDelay);
        }
        else if (_taskbarCreated != 0 && msg == _taskbarCreated && !IsFromOurTray())
        {
            Log.Info("Explorer restarted");
            Restart(_explorerDelay);
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// TaskbarCreated is also broadcast by ManagedShell (when its tray starts, and when we ask apps
    /// to re-add their icons). Only a restarted Explorer has a new taskbar window, so the message
    /// counts as an Explorer restart only when Explorer's taskbar window has changed since last time.
    /// </summary>
    bool IsFromOurTray()
    {
        var current = ExplorerTaskbarHandle();
        if (current == _lastExplorerTaskbar) return true;
        _lastExplorerTaskbar = current;
        return false;
    }

    IntPtr _lastExplorerTaskbar = ExplorerTaskbarHandle();

    static IntPtr ExplorerTaskbarHandle()
    {
        var ownPid = (uint)Environment.ProcessId;
        var found = IntPtr.Zero;
        for (var h = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null); h != IntPtr.Zero; h = FindWindowEx(IntPtr.Zero, h, "Shell_TrayWnd", null))
        {
            GetWindowThreadProcessId(h, out var pid);
            if (pid != ownPid) { found = h; break; }
        }
        return found;
    }

    public void Dispose() => _window.Dispose();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
