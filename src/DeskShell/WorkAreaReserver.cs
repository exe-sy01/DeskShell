using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using ManagedShell.AppBar;

namespace DeskShell;

/// <summary>
/// Keeps a strip at the bottom of each monitor free for the dock, so maximised windows stop above it.
///
/// Registering the dock as a Windows "app bar" (like the taskbar does) makes Explorer's own,
/// hidden taskbar renegotiate its space over and over, which flickers for a while on startup.
/// Instead the dock floats, and we set each monitor's usable area directly. If Explorer later
/// recalculates it (display changes, Explorer restarts), we put our strip back.
/// </summary>
internal sealed class WorkAreaReserver : IDisposable
{
    const int SPI_SETWORKAREA = 0x002F;
    const int SPIF_SENDCHANGE = 0x0002;
    const int WM_SETTINGCHANGE = 0x001A;

    readonly HwndSource _listener;
    readonly DispatcherTimer _debounce;
    List<(AppBarScreen Screen, int Reserve)> _targets = new();
    readonly Queue<long> _recentFixes = new();
    bool _active;

    public WorkAreaReserver()
    {
        _listener = new HwndSource(new HwndSourceParameters("DeskShellWorkArea") { Width = 0, Height = 0, WindowStyle = 0 });
        _listener.AddHook(WndProc);
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => Check(), Dispatcher.CurrentDispatcher);
        _debounce.Stop();
    }

    /// <summary>Reserves <paramref name="heightDip"/> at the bottom of each given monitor.</summary>
    public void Reserve(IEnumerable<AppBarScreen> screens, double heightDip)
    {
        _targets = screens.Select(s => (s, (int)Math.Round(heightDip * WindowManagement.WmNative.ScaleFor(s.HMonitor)))).ToList();
        _active = true;
        Apply();
    }

    /// <summary>Gives the whole screen back to windows.</summary>
    public void Release()
    {
        _active = false;
        _debounce.Stop();
        foreach (var (screen, _) in _targets)
        {
            var b = screen.Bounds;
            var full = new RECT { Left = b.Left, Top = b.Top, Right = b.Right, Bottom = b.Bottom };
            // Announce this one: on the way out we want Explorer to recalculate and claim its taskbar space.
            SystemParametersInfo(SPI_SETWORKAREA, 0, ref full, SPIF_SENDCHANGE);
        }
        _targets.Clear();
    }

    void Apply()
    {
        foreach (var (screen, reserve) in _targets)
        {
            var desired = Desired(screen, reserve);
            if (!Matches(screen.HMonitor, desired)) Set(desired);
        }
    }

    /// <summary>Full monitor minus our strip at the bottom (Explorer's hidden taskbar sits inside that strip).</summary>
    static RECT Desired(AppBarScreen screen, int reserve)
    {
        var b = screen.Bounds;
        return new RECT { Left = b.Left, Top = b.Top, Right = b.Right, Bottom = b.Bottom - reserve };
    }

    static bool Matches(IntPtr monitor, RECT desired)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return true;
        var w = info.rcWork;
        return w.Left == desired.Left && w.Top == desired.Top && w.Right == desired.Right && w.Bottom == desired.Bottom;
    }

    // No SPIF_SENDCHANGE: broadcasting the change makes Explorer re-lay-out its hidden taskbar,
    // which is exactly the flicker this class exists to avoid. Windows still applies the new area.
    static void Set(RECT rect) => SystemParametersInfo(SPI_SETWORKAREA, 0, ref rect, 0);

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SETTINGCHANGE && wParam.ToInt32() == SPI_SETWORKAREA && _active)
        {
            _debounce.Stop();
            _debounce.Start();
        }
        return IntPtr.Zero;
    }

    void Check()
    {
        _debounce.Stop();
        if (!_active || _targets.All(t => Matches(t.Screen.HMonitor, Desired(t.Screen, t.Reserve)))) return;

        // Something else changed the usable area; put our strip back, but never fight in a loop.
        var now = Environment.TickCount64;
        while (_recentFixes.Count > 0 && now - _recentFixes.Peek() > 10_000) _recentFixes.Dequeue();
        if (_recentFixes.Count >= 4)
        {
            Log.Error("The screen's usable area keeps changing; leaving it alone for now");
            return;
        }
        _recentFixes.Enqueue(now);
        Log.Info("Usable screen area was changed by something else; reserving the dock's strip again");
        Apply();
    }

    public void Dispose()
    {
        _debounce.Stop();
        _listener.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] static extern bool SystemParametersInfo(int action, int param, ref RECT rect, int winIni);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
