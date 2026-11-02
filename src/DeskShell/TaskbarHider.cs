using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace DeskShell;

/// <summary>
/// Hides the Windows taskbar without changing any of its settings.
///
/// Switching the taskbar to auto-hide (the usual trick, and what ManagedShell does) makes Explorer
/// animate and re-lay-out its taskbar every time anything nudges it, which flickered for seconds
/// on startup, and Explorer can save that auto-hide as if it were your own setting. So we leave
/// the taskbar's mode alone, just hide its windows, and WorkAreaReserver claims the screen space.
/// If Explorer shows a taskbar window again, we hide it again. Settings can leave it on screen instead.
/// </summary>
internal sealed class TaskbarHider
{
    readonly DispatcherTimer _timer;
    long _lastLogged;

    public TaskbarHider()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher.CurrentDispatcher);
        _timer.Stop();
    }

    public void Start()
    {
        foreach (var hwnd in ExplorerTaskbars())
            if (IsWindowVisible(hwnd)) ShowWindowAsync(hwnd, SW_HIDE);

        // Explorer sometimes re-shows a taskbar once right after the first hide; check quickly at
        // first so that blip is too short to see, then relax.
        _timer.Interval = TimeSpan.FromMilliseconds(30);
        _timer.Start();
        var relax = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        relax.Tick += (_, _) =>
        {
            relax.Stop();
            _timer.Interval = TimeSpan.FromMilliseconds(250);
        };
        relax.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        ShowAll();
    }

    /// <summary>Shows Explorer's taskbars again (also used by the crash watchdog).</summary>
    public static void ShowAll()
    {
        foreach (var hwnd in ExplorerTaskbars()) ShowWindowAsync(hwnd, SW_SHOW);
    }

    /// <summary>How many pixels of this monitor's bottom edge Explorer's taskbar covers.</summary>
    public static int BottomStrip(System.Drawing.Rectangle monitor)
    {
        var strip = 0;
        foreach (var hwnd in ExplorerTaskbars())
        {
            if (!GetWindowRect(hwnd, out var r)) continue;
            var centre = r.Left + (r.Right - r.Left) / 2;
            if (centre < monitor.Left || centre >= monitor.Right) continue;
            if (r.Bottom < monitor.Bottom || r.Top >= monitor.Bottom || r.Top <= monitor.Top) continue;
            strip = Math.Max(strip, monitor.Bottom - r.Top);
        }
        return strip;
    }

    void Tick()
    {
        try
        {
            foreach (var hwnd in ExplorerTaskbars())
            {
                if (!IsWindowVisible(hwnd)) continue;
                if (Environment.TickCount64 - _lastLogged > 30_000)
                {
                    _lastLogged = Environment.TickCount64;
                    Log.Info("Explorer showed its taskbar again; hiding it");
                }
                ShowWindowAsync(hwnd, SW_HIDE);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Hiding the taskbar failed", ex);
        }
    }

    /// <summary>Explorer's taskbar windows (not ManagedShell's own tray window, which shares the class name).</summary>
    static List<IntPtr> ExplorerTaskbars()
    {
        var result = new List<IntPtr>();
        var ownPid = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            var cls = new StringBuilder(64);
            GetClassName(hwnd, cls, cls.Capacity);
            if (cls.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid != ownPid) result.Add(hwnd);
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    const int SW_HIDE = 0, SW_SHOW = 5;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
}
