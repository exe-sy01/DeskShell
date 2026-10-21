using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace DeskShell.Services;

/// <summary>
/// Knows, per monitor, whether the top window on it fills the whole screen: a fullscreen or
/// borderless game, a fullscreen video, a browser in F11. Unlike ManagedShell's check, which only
/// looks at the focused window, this keeps a monitor covered while you use another monitor, so the
/// dock doesn't pop up over a borderless game when you click on your second screen.
/// </summary>
public sealed class FullscreenWatcher : IDisposable
{
    readonly DispatcherTimer _timer;
    readonly uint _ownPid = (uint)Environment.ProcessId;
    HashSet<IntPtr> _covered = new();

    /// <summary>Raised on the UI thread when a monitor becomes covered or uncovered.</summary>
    public event Action? Changed;

    public FullscreenWatcher()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Check(), Dispatcher.CurrentDispatcher);
        _timer.Start();
        Check();
    }

    public bool IsCovered(IntPtr monitor) => _covered.Contains(monitor);

    /// <summary>Checks now and again shortly (apps often go fullscreen just after they're activated).</summary>
    public void CheckSoon()
    {
        Check();
        var again = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
        again.Tick += (_, _) => { again.Stop(); Check(); };
        again.Start();
    }

    void Check()
    {
        var covered = new HashSet<IntPtr>();
        var decided = new HashSet<IntPtr>();
        // EnumWindows walks top-level windows from the top of the z-order down; the first real
        // window on each monitor decides whether that monitor is covered.
        EnumWindows((hwnd, _) =>
        {
            if (Ignore(hwnd, out var rect)) return true;
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (decided.Contains(monitor)) return true;
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info)) return true;
            var m = info.rcMonitor;
            if (rect.Left <= m.Left && rect.Top <= m.Top && rect.Right >= m.Right && rect.Bottom >= m.Bottom)
            {
                covered.Add(monitor);
                decided.Add(monitor);
            }
            // An always-on-top window that doesn't fill the screen (a mini player, a widget) floats
            // over whatever is underneath, so it doesn't decide.
            else if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) == 0)
            {
                decided.Add(monitor);
            }
            return true;
        }, IntPtr.Zero);

        if (covered.SetEquals(_covered)) return;
        _covered = covered;
        Log.Info($"Fullscreen: {covered.Count} monitor(s) covered");
        Changed?.Invoke();
    }

    bool Ignore(IntPtr hwnd, out RECT rect)
    {
        rect = default;
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
        // Overlays (Steam, Discord, NVIDIA) and tool windows: click-through, non-activating or tool-style
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((ex & (WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE)) != 0) return true;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == _ownPid) return true;
        var cls = new StringBuilder(64);
        GetClassName(hwnd, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf<RECT>()) != 0 && !GetWindowRect(hwnd, out rect)) return true;
        return rect.Right - rect.Left <= 0 || rect.Bottom - rect.Top <= 0;
    }

    public void Dispose() => _timer.Stop();

    const int GWL_EXSTYLE = -20;
    const int WS_EX_TOPMOST = 0x8;
    const int WS_EX_TRANSPARENT = 0x20;
    const int WS_EX_TOOLWINDOW = 0x80;
    const int WS_EX_NOACTIVATE = 0x8000000;
    const int MONITOR_DEFAULTTONEAREST = 2;
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    const int DWMWA_CLOAKED = 14;

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }
    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
}
