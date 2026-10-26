using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using ManagedShell.AppBar;

namespace DeskShell;

/// <summary>
/// Keeps strips of each monitor free for the island (top) and dock (bottom), so maximised windows
/// stay clear of them.
///
/// Registering the dock as a Windows "app bar" (like the taskbar does) makes Explorer's own,
/// hidden taskbar renegotiate its space over and over, which flickers for a while on startup.
/// Instead the dock floats, and we set each monitor's usable area directly. If Explorer later
/// recalculates it (display changes, Explorer restarts), we put our strip back.
/// </summary>
internal sealed class WorkAreaReserver : IDisposable
{
    const int SPI_SETWORKAREA = 0x002F;
    const int WM_SETTINGCHANGE = 0x001A;

    readonly HwndSource _listener;
    readonly DispatcherTimer _debounce;
    List<(AppBarScreen Screen, int Top, int Bottom)> _targets = new();
    readonly Queue<long> _recentFixes = new();
    bool _active;

    public WorkAreaReserver()
    {
        _listener = new HwndSource(new HwndSourceParameters("DeskShellWorkArea") { Width = 0, Height = 0, WindowStyle = 0 });
        _listener.AddHook(WndProc);
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => Check(), Dispatcher.CurrentDispatcher);
        _debounce.Stop();
    }

    /// <summary>Reserves strips (in device-independent pixels) at the top and bottom of each given monitor.</summary>
    public void Reserve(IEnumerable<(AppBarScreen Screen, double TopDip, double BottomDip)> screens)
    {
        _targets = screens.Select(s =>
        {
            var scale = WindowManagement.WmNative.ScaleFor(s.Screen.HMonitor);
            return (s.Screen, (int)Math.Round(s.TopDip * scale), (int)Math.Round(s.BottomDip * scale));
        }).ToList();
        _active = true;
        Apply();
    }

    /// <summary>Gives the whole screen back to windows.</summary>
    public void Release()
    {
        _active = false;
        _debounce.Stop();
        foreach (var (screen, _, _) in _targets)
        {
            var b = screen.Bounds;
            var full = new RECT { Left = b.Left, Top = b.Top, Right = b.Right, Bottom = b.Bottom };
            SystemParametersInfo(SPI_SETWORKAREA, 0, ref full, 0);
        }
        _targets.Clear();

        // Announce it so Explorer recalculates and claims its taskbar space. Sent with a timeout
        // (unlike SPIF_SENDCHANGE) so a hung window can never stall quitting.
        SendMessageTimeout(new IntPtr(0xffff), WM_SETTINGCHANGE, new IntPtr(SPI_SETWORKAREA), IntPtr.Zero, 0x0002 /* SMTO_ABORTIFHUNG */, 1000, out _);
    }

    void Apply()
    {
        foreach (var (screen, top, bottom) in _targets)
        {
            var desired = Desired(screen, top, bottom);
            if (!Matches(screen.HMonitor, desired)) Set(desired);
        }
    }

    /// <summary>Full monitor minus our strips (Explorer's hidden taskbar sits inside the bottom one).</summary>
    static RECT Desired(AppBarScreen screen, int top, int bottom)
    {
        var b = screen.Bounds;
        return new RECT { Left = b.Left, Top = b.Top + top, Right = b.Right, Bottom = b.Bottom - bottom };
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
        if (!_active || _targets.All(t => Matches(t.Screen.HMonitor, Desired(t.Screen, t.Top, t.Bottom)))) return;

        // Something else changed the usable area; put our strip back, but never fight in a loop.
        var now = Environment.TickCount64;
        while (_recentFixes.Count > 0 && now - _recentFixes.Peek() > 10_000) _recentFixes.Dequeue();
        if (_recentFixes.Count >= 4)
        {
            Log.Error("The screen's usable area keeps changing; leaving it alone for now");
            return;
        }
        _recentFixes.Enqueue(now);
        Log.Info("Usable screen area was changed by something else; reserving our strips again");
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
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
