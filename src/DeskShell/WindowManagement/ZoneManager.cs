using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ManagedShell.AppBar;
using static DeskShell.WindowManagement.WmNative;

namespace DeskShell.WindowManagement;

public sealed record ZoneLayout(string Id, string Name, Rect[] Zones);

/// <summary>
/// Snap zones: hold Shift while dragging a window and drop it on a zone to fill it.
/// Each monitor has its own layout; the "Window layout" tile and settings switch between them.
/// </summary>
public sealed class ZoneManager : IDisposable
{
    public static readonly ZoneLayout[] Layouts =
    [
        new("halves", "Two columns", [new(0, 0, .5, 1), new(.5, 0, .5, 1)]),
        new("thirds", "Three columns", [new(0, 0, 1 / 3.0, 1), new(1 / 3.0, 0, 1 / 3.0, 1), new(2 / 3.0, 0, 1 / 3.0, 1)]),
        new("focus", "Wide centre", [new(0, 0, .25, 1), new(.25, 0, .5, 1), new(.75, 0, .25, 1)]),
        new("main-side", "Main + side", [new(0, 0, .62, 1), new(.62, 0, .38, .5), new(.62, .5, .38, .5)]),
        new("grid", "Four quarters", [new(0, 0, .5, .5), new(.5, 0, .5, .5), new(0, .5, .5, .5), new(.5, .5, .5, .5)]),
        new("rows", "Two rows", [new(0, 0, 1, .5), new(0, .5, 1, .5)]),
    ];

    readonly DispatcherTimer _poll;
    readonly ZoneOverlay _overlay = new();
    IntPtr _dragging;
    int _highlight = -1;
    List<RECT> _zones = new();

    public ZoneManager()
    {
        _poll = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Normal, (_, _) => Poll(), Dispatcher.CurrentDispatcher);
        _poll.Stop();
    }

    public static List<AppBarScreen> ScreensLeftToRight() =>
        AppBarScreen.FromAllScreens().OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y).ToList();

    public static AppBarScreen ScreenAtCursor()
    {
        GetCursorPos(out var p);
        return AppBarScreen.FromAllScreens().FirstOrDefault(s => s.Bounds.Contains(p.X, p.Y)) ?? AppBarScreen.FromPrimaryScreen();
    }

    public static AppBarScreen ScreenOf(IntPtr hwnd)
    {
        var hmon = MonitorFromWindow(hwnd, 2);
        return AppBarScreen.FromAllScreens().FirstOrDefault(s => s.HMonitor == hmon) ?? AppBarScreen.FromPrimaryScreen();
    }

    public static ZoneLayout LayoutFor(AppBarScreen screen) =>
        Layouts.FirstOrDefault(l => App.Settings.ZoneLayouts.TryGetValue(screen.DeviceName, out var id) && l.Id == id) ?? Layouts[0];

    public string LayoutNameAtCursor() => LayoutFor(ScreenAtCursor()).Name;

    public void CycleLayoutAtCursor()
    {
        var screen = ScreenAtCursor();
        var i = Array.IndexOf(Layouts, LayoutFor(screen));
        App.Settings.ZoneLayouts[screen.DeviceName] = Layouts[(i + 1) % Layouts.Length].Id;
        App.Settings.Save();
    }

    /// <summary>Zone rectangles in screen pixels, inside the monitor's usable area, with gaps.</summary>
    public static List<RECT> ZonesFor(AppBarScreen screen)
    {
        var area = screen.WorkingArea;
        var gap = (int)Math.Round(App.Settings.ZoneGap * ScaleFor(screen.HMonitor));
        var half = gap / 2.0;
        return LayoutFor(screen).Zones.Select(z =>
        {
            // Outer edges get a full gap, shared inner edges get half each.
            var l = area.X + z.Left * area.Width + (z.Left <= 0 ? gap : half);
            var t = area.Y + z.Top * area.Height + (z.Top <= 0 ? gap : half);
            var r = area.X + z.Right * area.Width - (z.Right >= 0.999 ? gap : half);
            var b = area.Y + z.Bottom * area.Height - (z.Bottom >= 0.999 ? gap : half);
            return new RECT((int)l, (int)t, (int)r, (int)b);
        }).ToList();
    }

    /// <summary>Puts a window into zone N (1-based) of a monitor's layout.</summary>
    public static bool SnapToZone(IntPtr hwnd, AppBarScreen screen, int zone)
    {
        var zones = ZonesFor(screen);
        if (zone < 1 || zone > zones.Count) return false;
        PlaceWindow(hwnd, zones[zone - 1]);
        return true;
    }

    public void OnMoveSizeStart(IntPtr hwnd)
    {
        if (!App.Settings.SnapZones || IsOwnWindow(hwnd)) return;
        _dragging = hwnd;
        _highlight = -1;
        _poll.Start();
    }

    public void OnMoveSizeEnd(IntPtr hwnd)
    {
        if (_dragging == IntPtr.Zero) return;
        _poll.Stop();
        var target = _overlay.IsVisible && _highlight >= 0 && _highlight < _zones.Count ? _zones[_highlight] : (RECT?)null;
        _overlay.Hide();
        var window = _dragging;
        _dragging = IntPtr.Zero;
        if (target is { } rect)
            Application.Current.Dispatcher.BeginInvoke(() => PlaceWindow(window, rect), DispatcherPriority.Background);
    }

    void Poll()
    {
        var shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
        if (!shift)
        {
            if (_overlay.IsVisible) _overlay.Hide();
            _highlight = -1;
            return;
        }

        var screen = ScreenAtCursor();
        _zones = ZonesFor(screen);
        GetCursorPos(out var p);
        _highlight = _zones.FindIndex(z => p.X >= z.Left && p.X < z.Right && p.Y >= z.Top && p.Y < z.Bottom);
        _overlay.ShowZones(screen, _zones, _highlight);
    }

    public void Dispose()
    {
        _poll.Stop();
        _overlay.Close();
    }
}

/// <summary>Click-through overlay that draws the zones while you drag.</summary>
internal sealed class ZoneOverlay : Window
{
    readonly Canvas _canvas = new();
    string _screen = "";
    int _lastHighlight = -2;

    public ZoneOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Left = Top = -20000;
        Width = Height = 10;
        Content = _canvas;
        SourceInitialized += (_, _) =>
            AddExStyle(new WindowInteropHelper(this).Handle, WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    public void ShowZones(AppBarScreen screen, List<RECT> zones, int highlight)
    {
        if (!IsVisible) Show();
        var hwnd = new WindowInteropHelper(this).Handle;
        var b = screen.Bounds;
        if (_screen != screen.DeviceName)
        {
            SetWindowPos(hwnd, HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            _screen = screen.DeviceName;
            _lastHighlight = -2;
        }
        if (highlight == _lastHighlight) return;
        _lastHighlight = highlight;

        var scale = ScaleFor(screen.HMonitor);
        var accent = (Color)Application.Current.FindResource("AccentColor");
        _canvas.Children.Clear();
        for (var i = 0; i < zones.Count; i++)
        {
            var z = zones[i];
            var on = i == highlight;
            var box = new Border
            {
                Width = z.Width / scale,
                Height = z.Height / scale,
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(on ? accent : Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)),
                Background = new SolidColorBrush(on ? Color.FromArgb(0x55, accent.R, accent.G, accent.B) : Color.FromArgb(0x40, 0x10, 0x14, 0x18)),
                Child = new TextBlock
                {
                    Text = (i + 1).ToString(),
                    FontSize = 40,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromArgb(on ? (byte)0xFF : (byte)0xA0, 0xFF, 0xFF, 0xFF)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            Canvas.SetLeft(box, (z.Left - b.X) / scale);
            Canvas.SetTop(box, (z.Top - b.Y) / scale);
            _canvas.Children.Add(box);
        }
    }

    public new void Hide()
    {
        _screen = "";
        base.Hide();
    }
}
