using System.Runtime.InteropServices;

namespace DeskShell.WindowManagement;

/// <summary>System-wide window events (focus changes, drag start/end), delivered on the UI thread.</summary>
internal sealed class WindowEvents : IDisposable
{
    const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
    const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    const uint WINEVENT_OUTOFCONTEXT = 0;
    const int OBJID_WINDOW = 0;

    readonly WinEventProc _proc;
    readonly IntPtr _foregroundHook, _moveHook;

    public event Action<IntPtr>? Foreground;
    public event Action<IntPtr>? MoveSizeStart;
    public event Action<IntPtr>? MoveSizeEnd;

    public WindowEvents()
    {
        _proc = OnEvent; // keep the delegate alive for the life of the hooks
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        _moveHook = SetWinEventHook(EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    void OnEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != OBJID_WINDOW || hwnd == IntPtr.Zero) return;
        try
        {
            switch (evt)
            {
                case EVENT_SYSTEM_FOREGROUND: Foreground?.Invoke(hwnd); break;
                case EVENT_SYSTEM_MOVESIZESTART: MoveSizeStart?.Invoke(hwnd); break;
                case EVENT_SYSTEM_MOVESIZEEND: MoveSizeEnd?.Invoke(hwnd); break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Window event handler failed", ex);
        }
    }

    public void Dispose()
    {
        UnhookWinEvent(_foregroundHook);
        UnhookWinEvent(_moveHook);
    }

    delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);

    [DllImport("user32.dll")]
    static extern bool UnhookWinEvent(IntPtr hook);
}

/// <summary>Colours the border of whichever window has focus (Windows 11).</summary>
internal sealed class ActiveBorder
{
    IntPtr _current;

    public void OnForeground(IntPtr hwnd)
    {
        if (!App.Settings.ActiveWindowBorder) return;
        hwnd = WmNative.GetAncestor(hwnd, WmNative.GA_ROOT);
        if (hwnd == _current || WmNative.IsOwnWindow(hwnd)) return;

        Reset();
        var color = ParseColor(App.Settings.BorderColor);
        if (WmNative.DwmSetWindowAttribute(hwnd, WmNative.DWMWA_BORDER_COLOR, ref color, sizeof(uint)) == 0)
            _current = hwnd;
    }

    public void Reset()
    {
        if (_current == IntPtr.Zero) return;
        var none = WmNative.DWMWA_COLOR_DEFAULT;
        if (WmNative.IsWindow(_current))
            WmNative.DwmSetWindowAttribute(_current, WmNative.DWMWA_BORDER_COLOR, ref none, sizeof(uint));
        _current = IntPtr.Zero;
    }

    /// <summary>"#RRGGBB" → Windows COLORREF (0x00BBGGRR).</summary>
    static uint ParseColor(string hex)
    {
        try
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            return (uint)(c.R | (c.G << 8) | (c.B << 16));
        }
        catch
        {
            return 0x00C2B83F;
        }
    }
}

/// <summary>Remembers which windows were used most recently, for Alt-Tab ordering.</summary>
internal sealed class WindowMru
{
    readonly LinkedList<IntPtr> _order = new();

    public void Touch(IntPtr hwnd)
    {
        hwnd = WmNative.GetAncestor(hwnd, WmNative.GA_ROOT);
        if (WmNative.IsOwnWindow(hwnd)) return;
        _order.Remove(hwnd);
        _order.AddFirst(hwnd);
        while (_order.Count > 200) _order.RemoveLast();
    }

    public int RankOf(IntPtr hwnd)
    {
        var i = 0;
        for (var node = _order.First; node != null; node = node.Next, i++)
            if (node.Value == hwnd) return i;
        return int.MaxValue;
    }
}
