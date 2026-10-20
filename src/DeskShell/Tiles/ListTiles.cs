using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DeskShell.Services;
using ManagedShell.Common.Helpers;
using NotifyIcon = ManagedShell.WindowsTray.NotifyIcon;
using NativeRect = ManagedShell.Interop.NativeMethods.Rect;

namespace DeskShell.Tiles;

/// <summary>Tile with a heading row and scrollable content below.</summary>
public abstract class PanelTile : TileBase
{
    protected readonly StackPanel HeaderActions = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    protected readonly TextBlock HeaderText;
    readonly ContentControl _body = new();
    readonly Grid _header;

    protected bool HeaderVisible { set => _header.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }

    protected PanelTile(ShellContext c, string heading) : base(c)
    {
        Padding = new Thickness(12, 8, 8, 10);
        HeaderText = Text(heading, 12.5, bold: true);
        HeaderText.Margin = new Thickness(2, 0, 0, 0);
        _header = new Grid { Height = 30, Margin = new Thickness(0, 0, 0, 4) };
        _header.Children.Add(HeaderText);
        _header.Children.Add(HeaderActions);
        var dock = new DockPanel();
        DockPanel.SetDock(_header, Dock.Top);
        dock.Children.Add(_header);
        dock.Children.Add(_body);
        Child = dock;
    }

    protected UIElement Body { set => _body.Content = value; }

    protected static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(0, 0, 4, 0)
    };
}

public sealed class CalendarTile : PanelTile
{
    readonly UniformGrid _days = new() { Columns = 7 };
    DateTime _month = DateTime.Today;
    static DayOfWeek FirstDay => CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;

    public CalendarTile(ShellContext c) : base(c, "")
    {
        HeaderActions.Children.Add(IconButton("", "Previous month", (_, _) => Move(-1), 28));
        HeaderActions.Children.Add(IconButton("", "Next month", (_, _) => Move(1), 28));

        var names = new UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 0, 2) };
        var shortNames = CultureInfo.CurrentCulture.DateTimeFormat.ShortestDayNames;
        for (var i = 0; i < 7; i++)
        {
            var t = Text(shortNames[((int)FirstDay + i) % 7], 11, dim: true);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            names.Children.Add(t);
        }
        var stack = new StackPanel();
        stack.Children.Add(names);
        stack.Children.Add(_days);
        Body = stack;
    }

    public override string Id => "calendar";
    public override string Title => "Calendar";
    public override (int W, int H)[] Sizes => [(2, 3)];

    public override void OnOpened()
    {
        _month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        Build();
    }

    void Move(int months)
    {
        _month = _month.AddMonths(months);
        Build();
    }

    void Build()
    {
        HeaderText.Text = _month.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        _days.Children.Clear();
        var offset = ((int)_month.DayOfWeek - (int)FirstDay + 7) % 7;
        var count = DateTime.DaysInMonth(_month.Year, _month.Month);
        for (var cell = 0; cell < 42; cell++)
        {
            var day = cell - offset + 1;
            var inMonth = day >= 1 && day <= count;
            var today = inMonth && _month.AddDays(day - 1) == DateTime.Today;
            var label = Text(inMonth ? day.ToString(CultureInfo.CurrentCulture) : "", 12.5, bold: today);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            if (today) label.Foreground = Res<Brush>("OnAccentBrush");
            _days.Children.Add(new Border
            {
                Width = 28,
                Height = 28,
                Margin = new Thickness(0, 1, 0, 1),
                CornerRadius = new CornerRadius(14),
                Background = today ? Res<Brush>("AccentBrush") : Brushes.Transparent,
                Child = label
            });
        }
    }
}

public sealed class ClipboardTile : PanelTile
{
    readonly StackPanel _list = new();
    readonly TextBlock _empty = Text("Text you copy shows up here. Click an item to copy it again.", 12, dim: true);

    public ClipboardTile(ShellContext c) : base(c, "Clipboard")
    {
        _empty.TextWrapping = TextWrapping.Wrap;
        _empty.TextTrimming = TextTrimming.None;
        HeaderActions.Children.Add(IconButton("", "Clear history", (_, _) => { Context.Clipboard.Clear(); Build(); }, 28));
        var stack = new StackPanel();
        stack.Children.Add(_empty);
        stack.Children.Add(_list);
        Body = Scroll(stack);
        c.Clipboard.Items.CollectionChanged += (_, _) => { if (IsVisible) Build(); };
    }

    public override string Id => "clipboard";
    public override string Title => "Clipboard history";
    public override (int W, int H)[] Sizes => [(2, 2), (2, 3), (4, 2)];

    public override void OnOpened() => Build();

    void Build()
    {
        _list.Children.Clear();
        _empty.Visibility = Context.Clipboard.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var text in Context.Clipboard.Items.ToList())
        {
            var preview = text.ReplaceLineEndings(" ").Trim();
            if (preview.Length > 200) preview = preview[..200];
            var row = new Button { Style = Res<Style>("ListRow"), Content = Text(preview, 12.5), ToolTip = text.Length > 600 ? text[..600] + "…" : text };
            row.Click += (_, _) =>
            {
                Context.Clipboard.Restore(text);
                HeaderText.Text = "Copied";
                Dispatcher.InvokeAsync(async () => { await Task.Delay(1200); HeaderText.Text = "Clipboard"; });
            };
            _list.Children.Add(row);
        }
    }
}

public sealed class NotificationsTile : PanelTile
{
    readonly StackPanel _list = new();
    readonly TextBlock _empty = Text("No notifications", 12, dim: true);

    public NotificationsTile(ShellContext c) : base(c, "Notifications")
    {
        HeaderActions.Children.Add(IconButton("", "Open Windows notification center", (_, _) =>
        {
            CloseShell();
            ShellHelper.ShowNotificationCenter();
        }, 28));
        var stack = new StackPanel();
        stack.Children.Add(_empty);
        stack.Children.Add(_list);
        Body = Scroll(stack);
    }

    public override string Id => "notifications";
    public override string Title => "Notifications";
    public override (int W, int H)[] Sizes => [(2, 2), (2, 3), (4, 2)];

    public override async void OnOpened()
    {
        var items = await NotificationReader.ReadAsync();
        _list.Children.Clear();
        _empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var n in items)
        {
            var app = Context.Apps.Find(n.AppId);
            var icon = new Image { Source = app?.Icon, Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 8, 0) };
            var title = Text(n.Title, 12.5, bold: true);
            var when = Text(Ago(n.Arrived), 11, dim: true);
            when.HorizontalAlignment = HorizontalAlignment.Right;
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition());
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(when, 1);
            top.Children.Add(title);
            top.Children.Add(when);
            var body = Text(n.Body, 12, dim: true);
            body.TextWrapping = TextWrapping.Wrap;
            body.MaxHeight = 34;
            var text = new StackPanel();
            text.Children.Add(Text(app?.Name ?? "", 11, dim: true));
            text.Children.Add(top);
            if (n.Body.Length > 0) text.Children.Add(body);
            var content = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left);
            content.Children.Add(icon);
            content.Children.Add(text);
            var row = new Button { Style = Res<Style>("ListRow"), Content = content };
            row.Click += (_, _) =>
            {
                CloseShell();
                ShellHelper.ShowNotificationCenter();
            };
            _list.Children.Add(row);
        }
    }

    static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}h";
        return $"{(int)d.TotalDays}d";
    }
}

public sealed class TrayTile : PanelTile
{
    readonly ItemsControl _icons;

    public TrayTile(ShellContext c) : base(c, "Background apps")
    {
        _icons = new ItemsControl
        {
            ItemTemplate = Res<DataTemplate>("TrayIconTemplate"),
            ItemsPanel = Res<ItemsPanelTemplate>("WrapItemsPanel"),
            ItemsSource = c.Shell.NotificationArea.AllIcons
        };
        _icons.PreviewMouseDown += (_, e) => Forward(e, down: true);
        _icons.PreviewMouseUp += (_, e) => Forward(e, down: false);
        _icons.MouseMove += (_, e) => Hover(e);
        Body = Scroll(_icons);
    }

    public override string Id => "tray";
    public override string Title => "Background apps (tray)";
    public override (int W, int H)[] Sizes => [(2, 2), (2, 1), (4, 1), (4, 2)];

    NotifyIcon? _hovered;

    static (FrameworkElement? Element, NotifyIcon? Icon) Find(object source)
    {
        for (var d = source as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { DataContext: NotifyIcon icon } fe && fe.Name == "TrayBg") return (fe, icon);
        return (null, null);
    }

    void Hover(MouseEventArgs e)
    {
        var (element, icon) = Find(e.OriginalSource);
        if (icon == _hovered) return;
        _hovered?.IconMouseLeave(MouseHelper.GetCursorPositionParam());
        _hovered = icon;
        if (icon == null || element == null) return;
        var a = element.PointToScreen(new Point(0, 0));
        var b = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        icon.Placement = new NativeRect((int)a.X, (int)a.Y, (int)b.X, (int)b.Y);
        icon.IconMouseEnter(MouseHelper.GetCursorPositionParam());
    }

    void Forward(MouseButtonEventArgs e, bool down)
    {
        var (_, icon) = Find(e.OriginalSource);
        if (icon == null) return;
        e.Handled = true;
        var pos = MouseHelper.GetCursorPositionParam();
        var dbl = (int)Native.GetDoubleClickTime();
        if (down)
        {
            icon.IconMouseDown(e.ChangedButton, pos, dbl);
        }
        else if (e.ChangedButton == MouseButton.Right)
        {
            // Right-click opens the app's own menu (Quit, Settings…). Keep the shell open, but let the menu sit above it.
            Context.BeginAppMenu();
            icon.IconMouseUp(e.ChangedButton, pos, dbl);
        }
        else
        {
            // Left-click usually opens the app's window; get out of its way.
            CloseShell();
            icon.IconMouseUp(e.ChangedButton, pos, dbl);
        }
    }

    public override void OnClosed()
    {
        _hovered?.IconMouseLeave(MouseHelper.GetCursorPositionParam());
        _hovered = null;
    }
}

public sealed class PinnedTile : PanelTile
{
    readonly WrapPanel _apps = new();
    readonly TextBlock _empty = Text("Search for an app above, right-click it and choose \"Pin\" to keep it here.", 12, dim: true);

    public PinnedTile(ShellContext c) : base(c, "Pinned")
    {
        _empty.TextWrapping = TextWrapping.Wrap;
        _empty.TextTrimming = TextTrimming.None;
        var stack = new StackPanel();
        stack.Children.Add(_empty);
        stack.Children.Add(_apps);
        Body = Scroll(stack);
        Padding = new Thickness(10, 2, 8, 6);
    }

    public override string Id => "pinned";
    public override string Title => "Pinned apps";
    public override (int W, int H)[] Sizes => [(4, 1), (2, 1), (4, 2), (2, 2)];

    public override void OnOpened() => Build();

    void Build()
    {
        _apps.Children.Clear();
        var pinned = App.Settings.PinnedApps;
        _empty.Visibility = pinned.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Once something is pinned the apps speak for themselves; the heading would push them out of a one-row tile.
        HeaderVisible = pinned.Count == 0;
        _apps.VerticalAlignment = VerticalAlignment.Center;
        foreach (var p in pinned.ToList())
        {
            var found = Context.Apps.Find(p.Target);
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new Image { Source = found?.Icon, Width = 22, Height = 22 });
            var name = Text(p.Name, 12.5);
            name.MaxWidth = 120;
            name.Margin = new Thickness(8, 0, 2, 0);
            content.Children.Add(name);
            var button = new Button { Style = Res<Style>("ChipButton"), Content = content, ToolTip = p.Name, Margin = new Thickness(0, 0, 6, 6) };
            var target = p.Target;
            button.Click += (_, _) =>
            {
                CloseShell();
                AppIndex.Launch(target);
            };
            var menu = new ContextMenu();
            var unpin = new MenuItem { Header = "Unpin" };
            unpin.Click += (_, _) =>
            {
                App.Settings.PinnedApps.RemoveAll(x => x.Target == target);
                App.Settings.Save();
                Build();
            };
            menu.Items.Add(unpin);
            button.ContextMenu = menu;
            _apps.Children.Add(button);
        }
    }
}

public static class TileFactory
{
    public static readonly string[] AllIds =
        ["pinned", "wifi", "bluetooth", "focus", "darkmode", "screenshot", "layouts", "volume", "brightness",
         "media", "audioout", "stats", "weather", "calendar", "clipboard", "notifications", "tray"];

    public static readonly Dictionary<string, string> Titles = new()
    {
        ["pinned"] = "Pinned apps", ["wifi"] = "Wi-Fi / network", ["bluetooth"] = "Bluetooth", ["focus"] = "Do not disturb",
        ["darkmode"] = "Dark mode", ["screenshot"] = "Screenshot", ["layouts"] = "Window layout", ["volume"] = "Volume",
        ["brightness"] = "Brightness", ["media"] = "Now playing", ["audioout"] = "Sound output", ["stats"] = "CPU / memory / GPU",
        ["weather"] = "Weather", ["calendar"] = "Calendar", ["clipboard"] = "Clipboard history", ["notifications"] = "Notifications",
        ["tray"] = "Background apps (tray)",
    };

    public static TileBase? Create(string id, ShellContext c) => id switch
    {
        "pinned" => new PinnedTile(c),
        "wifi" => new WifiTile(c),
        "bluetooth" => new BluetoothTile(c),
        "focus" => new FocusTile(c),
        "darkmode" => new DarkModeTile(c),
        "screenshot" => new ScreenshotTile(c),
        "layouts" => new LayoutsTile(c),
        "volume" => new VolumeTile(c),
        "brightness" => new BrightnessTile(c),
        "media" => new MediaTile(c),
        "audioout" => new AudioOutputTile(c),
        "stats" => new StatsTile(c),
        "weather" => new WeatherTile(c),
        "calendar" => new CalendarTile(c),
        "clipboard" => new ClipboardTile(c),
        "notifications" => new NotificationsTile(c),
        "tray" => new TrayTile(c),
        _ => null
    };
}
