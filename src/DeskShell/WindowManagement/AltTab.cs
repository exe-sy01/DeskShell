using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ManagedShell;
using ManagedShell.WindowsTasks;
using static DeskShell.WindowManagement.WmNative;

namespace DeskShell.WindowManagement;

/// <summary>
/// Watches the keyboard for Alt+Tab on its own thread (so a busy UI can never lag your typing)
/// and hands the switching to <see cref="AltTabController"/>. If DeskShell's UI stops responding,
/// or a fullscreen game is running, Alt+Tab goes to Windows as normal.
/// </summary>
internal sealed class AltTabHook : IDisposable
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    const uint LLKHF_INJECTED = 0x10;
    const int VK_TAB = 0x09, VK_ESCAPE = 0x1B, VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_CONTROL = 0x11;
    const int VK_SHIFT = 0x10, VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_MENU = 0x12, VK_LMENU = 0xA4, VK_RMENU = 0xA5;
    const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    const uint KEYEVENTF_EXTENDEDKEY = 1, KEYEVENTF_KEYUP = 2;
    const uint WM_QUIT = 0x12;
    const byte HelperKey = 0xE8; // unassigned virtual key

    readonly Dispatcher _ui;
    readonly Thread _thread;
    uint _threadId;
    HookProc? _proc;
    IntPtr _hook;
    bool _alt, _shift, _active;
    bool _winDown, _winAlone;
    long _winLast;

    public volatile bool Enabled = true;
    public volatile bool Suspended;
    /// <summary>A tap of the Windows key raises <see cref="WinTapped"/> instead of opening the Start menu.</summary>
    public volatile bool CatchWinKey;
    long _heartbeat = Environment.TickCount64;

    public event Action<int>? Opened;
    public event Action<int>? Moved;
    public event Action? Committed;
    public event Action? Cancelled;
    public event Action? WinTapped;

    public AltTabHook(Dispatcher ui)
    {
        _ui = ui;
        _thread = new Thread(Run) { IsBackground = true, Name = "DeskShell keyboard hook" };
        _thread.Start();
    }

    /// <summary>The UI calls this regularly to show it's alive.</summary>
    public void Beat() => Interlocked.Exchange(ref _heartbeat, Environment.TickCount64);

    /// <summary>The switcher was closed some other way (e.g. a mouse click).</summary>
    public void Reset() => _active = false;

    void Run()
    {
        _threadId = GetCurrentThreadId();
        _proc = Proc;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            Log.Error($"Keyboard hook failed: {Marshal.GetLastWin32Error()}");
            return;
        }
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        UnhookWindowsHookEx(_hook);
    }

    IntPtr Proc(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0)
            {
                var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                // Ignore only our own helper key; other injected input (key remappers, etc.) is handled normally.
                // Injected Windows-key events (our own release below, or a key remapper's) aren't a tap either.
                var injected = (kb.flags & LLKHF_INJECTED) != 0;
                var ownKey = injected && kb.vkCode is HelperKey or VK_LWIN or VK_RWIN;
                if (!ownKey && Handle((int)wParam, (int)kb.vkCode))
                    return new IntPtr(1); // swallow
            }
        }
        catch
        {
            // Never block the keyboard because of a bug here.
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    bool Handle(int msg, int vk)
    {
        var down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
        var up = msg is WM_KEYUP or WM_SYSKEYUP;

        if (vk is VK_LWIN or VK_RWIN) return HandleWinKey(vk, down, up);
        if (down) _winAlone = false; // Win+something: a shortcut, not a tap

        if (vk is VK_MENU or VK_LMENU or VK_RMENU)
        {
            if (down) _alt = true;
            if (up)
            {
                _alt = false;
                if (_active)
                {
                    _active = false;
                    _ui.BeginInvoke(() => Committed?.Invoke());
                }
            }
            return false;
        }
        if (vk is VK_SHIFT or VK_LSHIFT or VK_RSHIFT)
        {
            if (down) _shift = true;
            if (up) _shift = false;
            return false;
        }

        if (vk == VK_TAB && _alt)
        {
            if (!_active)
            {
                if (!down) return false;
                var uiAlive = Environment.TickCount64 - Interlocked.Read(ref _heartbeat) < 2000;
                var ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                if (!Enabled || Suspended || !uiAlive || ctrl) return false;
                _active = true;
                // A harmless fake key stops the app behind from treating the Alt release as "open the menu bar".
                keybd_event(HelperKey, 0, 0, UIntPtr.Zero);
                keybd_event(HelperKey, 0, 2, UIntPtr.Zero);
                var dir = _shift ? -1 : 1;
                _ui.BeginInvoke(() => Opened?.Invoke(dir));
                return true;
            }
            if (down)
            {
                var dir = _shift ? -1 : 1;
                _ui.BeginInvoke(() => Moved?.Invoke(dir));
            }
            return true;
        }

        if (_active && down)
        {
            switch (vk)
            {
                case VK_ESCAPE:
                    _active = false;
                    _ui.BeginInvoke(() => Cancelled?.Invoke());
                    return true;
                case VK_LEFT:
                    _ui.BeginInvoke(() => Moved?.Invoke(-1));
                    return true;
                case VK_RIGHT:
                    _ui.BeginInvoke(() => Moved?.Invoke(1));
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The Windows key always goes through when pressed, so Win+ shortcuts keep working. Only when
    /// it's released with nothing pressed in between does Windows open the Start menu, so that
    /// release is held back: a harmless fake key first makes it look like a shortcut, then the
    /// release is sent on and the drawer opens instead. Left to Windows during games and if
    /// DeskShell's UI isn't responding.
    /// </summary>
    bool HandleWinKey(int vk, bool down, bool up)
    {
        var now = Environment.TickCount64;
        if (down)
        {
            // Held keys repeat; a long gap means the release was missed (e.g. Win+L locked the PC).
            if (!_winDown || now - _winLast > 1000)
            {
                _winDown = true;
                _winAlone = true;
            }
            _winLast = now;
            return false;
        }
        if (!up) return false;

        var tap = _winDown && _winAlone;
        _winDown = _winAlone = false;
        if (!tap || !CatchWinKey || Suspended) return false;
        if (Environment.TickCount64 - Interlocked.Read(ref _heartbeat) >= 2000) return false;
        if (new[] { VK_CONTROL, VK_SHIFT, VK_MENU }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0)) return false; // Ctrl+Win etc. stay with Windows

        keybd_event(HelperKey, 0, 0, UIntPtr.Zero);
        keybd_event(HelperKey, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event((byte)vk, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
        _ui.BeginInvoke(() => WinTapped?.Invoke());
        return true;
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
}

/// <summary>The Alt-Tab switcher: previews of your windows, most recently used first.</summary>
internal sealed class AltTabWindow : ThumbnailWindow
{
    const int PerRow = 6;
    readonly WrapPanel _panel = new() { MaxWidth = PerRow * 252 };
    int _selected;

    public event Action<IntPtr>? Picked;

    public AltTabWindow()
    {
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowActivated = false;
        SetResourceReference(BackgroundProperty, "CardBrush");
        Content = new Border { Padding = new Thickness(14), Child = _panel };
        SourceInitialized += (_, _) =>
        {
            AddExStyle(Hwnd, WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            SetCorners(Hwnd, DWMWCP_ROUND);
        };
        SizeChanged += (_, _) => Center();
    }

    public bool IsOpen { get; private set; }

    public void Open(List<ApplicationWindow> windows, int selected)
    {
        ClearCards();
        _panel.Children.Clear();
        foreach (var w in windows)
        {
            var card = MakeCard(w, 240, 170, closeButton: false);
            card.Root.Margin = new Thickness(6);
            _panel.Children.Add(card.Root);
        }
        _selected = Math.Clamp(selected, 0, windows.Count - 1);
        Highlight(Cards[_selected]);
        IsOpen = true;
        new WindowInteropHelper(this).EnsureHandle();
        Show();
        Center();
        Dispatcher.InvokeAsync(RegisterThumbnails, DispatcherPriority.Loaded);
    }

    void Center()
    {
        if (!IsOpen || Hwnd == IntPtr.Zero) return;
        var screen = ZoneManager.ScreenAtCursor();
        var scale = ScaleFor(screen.HMonitor);
        var w = (int)(ActualWidth * scale);
        var h = (int)(ActualHeight * scale);
        var b = screen.Bounds;
        SetWindowPos(Hwnd, HWND_TOPMOST, b.X + (b.Width - w) / 2, b.Y + (b.Height - h) / 2, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    public void Move(int direction)
    {
        if (Cards.Count == 0) return;
        _selected = (_selected + direction + Cards.Count) % Cards.Count;
        Highlight(Cards[_selected]);
    }

    public IntPtr Selected => Cards.ElementAtOrDefault(_selected)?.Window.Handle ?? IntPtr.Zero;

    public void CloseSwitcher()
    {
        IsOpen = false;
        ClearCards();
        _panel.Children.Clear();
        Hide();
    }

    protected override void OnHover(Card card)
    {
        _selected = Cards.IndexOf(card);
        Highlight(card);
    }

    protected override void OnPick(Card card) => Picked?.Invoke(card.Window.Handle);
}

/// <summary>Connects the keyboard hook, the window list and the switcher window.</summary>
internal sealed class AltTabController : IDisposable
{
    readonly ShellManager _shell;
    readonly WindowMru _mru;
    readonly AltTabHook _hook;
    readonly AltTabWindow _window = new();
    readonly DispatcherTimer _heartbeat;

    public AltTabController(ShellManager shell, WindowMru mru)
    {
        _shell = shell;
        _mru = mru;
        _hook = new AltTabHook(Dispatcher.CurrentDispatcher) { Enabled = App.Settings.CustomAltTab };
        _hook.Opened += Open;
        _hook.Moved += d => _window.Move(d);
        _hook.Committed += Commit;
        _hook.Cancelled += () => _window.CloseSwitcher();
        _window.Picked += hwnd =>
        {
            _hook.Reset();
            _window.CloseSwitcher();
            Activate(hwnd);
        };

        _hook.WinTapped += () => WinKeyTapped?.Invoke();
        _heartbeat = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Normal, (_, _) => _hook.Beat(), Dispatcher.CurrentDispatcher);
        // Windows' own Alt+Tab while a game or video is in front.
        if (App.Fullscreen != null)
            App.Fullscreen.Changed += () => _hook.Suspended = App.Fullscreen.ForegroundFullscreen;
    }

    public bool Enabled { set => _hook.Enabled = value; }

    /// <summary>Whether a tap of the Windows key raises <see cref="WinKeyTapped"/>.</summary>
    public bool CatchWinKey { set => _hook.CatchWinKey = value; }

    public event Action? WinKeyTapped;

    void Open(int direction)
    {
        // Most recently used first; windows we haven't seen used yet follow in front-to-back order.
        var z = ZOrder();
        var windows = _shell.Tasks.GroupedWindows?.OfType<ApplicationWindow>()
            .Where(w => w.ShowInTaskbar && IsWindow(w.Handle))
            .OrderBy(w => _mru.RankOf(w.Handle))
            .ThenBy(w => z.TryGetValue(w.Handle, out var i) ? i : int.MaxValue)
            .ToList() ?? new();
        if (windows.Count == 0)
        {
            _hook.Reset();
            return;
        }
        App.Instance.HideShell();
        var start = windows.Count == 1 ? 0 : direction > 0 ? 1 : windows.Count - 1;
        _window.Open(windows, start);
    }

    void Commit()
    {
        if (!_window.IsOpen) return;
        var hwnd = _window.Selected;
        _window.CloseSwitcher();
        Activate(hwnd);
    }

    static void Activate(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero && IsWindow(hwnd)) WmNative.Activate(hwnd);
    }

    public void Dispose()
    {
        _heartbeat.Stop();
        _hook.Dispose();
        _window.Close();
    }
}
