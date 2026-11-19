using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskShell.Services;
using ManagedShell.Common.Helpers;
using ManagedShell.WindowsTray;

namespace DeskShell.Notch;

/// <summary>
/// The notch's Background apps tab: the tray icons the taskbar would show (Discord, Steam, OneDrive…).
/// Clicks go to the app as if you'd clicked its icon in the taskbar: left-click usually opens its window,
/// right-click its menu (Quit, Settings…).
/// </summary>
sealed class TrayPage(NotchContext c) : NotchPage(c)
{
    public override string Id => "tray";
    public override string Title => "Background apps";
    public override string Glyph => "";

    readonly TextBlock _count = UI.Label("", 12.5, brush: UI.Dim);
    readonly WrapPanel _grid = new();
    static readonly Dictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase);
    string _shown = "";
    bool _listening;

    protected override UIElement Build()
    {
        _count.Margin = new Thickness(2, 0, 0, 10);
        var page = new DockPanel();
        DockPanel.SetDock(_count, Dock.Top);
        page.Children.Add(_count);
        page.Children.Add(new ScrollViewer { Content = _grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return page;
    }

    IEnumerable<NotifyIcon> Icons() =>
        Context.Shell.NotificationArea.AllIcons?.OfType<NotifyIcon>() ?? Context.Shell.NotificationArea.TrayIcons;

    public override void Refresh()
    {
        if (!_listening)
        {
            _listening = true;
            Context.Shell.NotificationArea.TrayIcons.CollectionChanged += (_, _) => { if (_grid.IsVisible) Tick(); };
        }
        _shown = "";
        Tick();
    }

    /// <summary>Rebuilds only when icons come, go, or change their picture or text.</summary>
    public override void Tick()
    {
        var icons = Icons().Where(i => i.Icon != null).ToList();
        var signature = string.Join("|", icons.Select(i => $"{i.Identifier}:{i.Title}:{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(i.Icon)}"));
        if (signature == _shown) return;
        _shown = signature;
        _count.Text = icons.Count switch
        {
            0 => "Nothing running in the background",
            1 => "1 app running in the background · click to open, right-click for its menu",
            var n => $"{n} apps running in the background · click to open, right-click for its menu"
        };
        _grid.Children.Clear();
        foreach (var icon in icons) _grid.Children.Add(Tile(icon));
    }

    /// <summary>The app's name: the first line of its icon's tooltip, else its program's description.</summary>
    static (string Name, string Detail) Words(NotifyIcon icon)
    {
        var lines = (icon.Title ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var program = Program(icon.Path);
        if (lines.Length == 0) return (program, "");
        var detail = string.Join(" · ", lines.Skip(1));
        // A tooltip that's only a status ("Connected", "3 unread") reads better under the program's name.
        if (program.Length > 0 && !lines[0].Contains(program, StringComparison.OrdinalIgnoreCase) && !program.Contains(lines[0], StringComparison.OrdinalIgnoreCase) && lines[0].Length < 40)
            return (lines[0], detail.Length > 0 ? detail : program);
        return (lines[0], detail);
    }

    static string Program(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        if (Descriptions.TryGetValue(path, out var known)) return known;
        var name = "";
        try { name = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() ?? ""; }
        catch { /* gone or unreadable */ }
        if (name.Length == 0) name = Path.GetFileNameWithoutExtension(path);
        return Descriptions[path] = name;
    }

    Button Tile(NotifyIcon icon)
    {
        var (name, detail) = Words(icon);
        var picture = new Image { Source = icon.Icon, Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var title = UI.Label(name.Length > 0 ? name : "Background app", 12.5, bold: true);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        words.Children.Add(title);
        if (detail.Length > 0)
        {
            var sub = UI.Label(detail, 11.5, brush: UI.Dim);
            sub.TextTrimming = TextTrimming.CharacterEllipsis;
            words.Children.Add(sub);
        }
        var row = new DockPanel();
        DockPanel.SetDock(picture, Dock.Left);
        row.Children.Add(picture);
        row.Children.Add(words);
        var card = new Border
        {
            Width = 228, Height = 56, Margin = new Thickness(0, 0, 9, 9), Padding = new Thickness(14, 6, 12, 6), CornerRadius = new CornerRadius(14),
            Background = UI.Card, BorderBrush = UI.CardEdge, BorderThickness = new Thickness(1), Child = row
        };
        var button = new Button { Content = card, Template = UI.Bare(), Cursor = Cursors.Hand, Focusable = false, ToolTip = string.IsNullOrWhiteSpace(icon.Title) ? name : icon.Title };
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        button.MouseEnter += (_, _) => card.Background = UI.Raised;
        button.MouseLeave += (_, _) => card.Background = UI.Card;

        // Tray apps place their menus and windows next to where they think their icon is.
        void Place()
        {
            if (PresentationSource.FromVisual(card) == null) return;
            var a = card.PointToScreen(new Point(0, 0));
            var b = card.PointToScreen(new Point(card.ActualWidth, card.ActualHeight));
            icon.Placement = new ManagedShell.Interop.NativeMethods.Rect((int)a.X, (int)a.Y, (int)b.X, (int)b.Y);
        }
        button.PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right or MouseButton.Middle)) return;
            e.Handled = true;
            Place();
            icon.IconMouseDown(e.ChangedButton, MouseHelper.GetCursorPositionParam(), (int)Native.GetDoubleClickTime());
        };
        button.PreviewMouseUp += (_, e) =>
        {
            if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right or MouseButton.Middle)) return;
            e.Handled = true;
            // Right-click: the app's menu, with the panel staying open just below it.
            // Otherwise the app usually opens its window: get out of its way.
            if (e.ChangedButton == MouseButton.Right) Context.BeginAppMenu(icon.HWnd);
            else Context.Collapse();
            icon.IconMouseUp(e.ChangedButton, MouseHelper.GetCursorPositionParam(), (int)Native.GetDoubleClickTime());
        };
        return button;
    }
}
