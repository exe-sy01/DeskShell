using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskShell.Services;

namespace DeskShell.Notch;

/// <summary>What the panel's pages need from DeskShell.</summary>
sealed class NotchContext
{
    public required ManagedShell.ShellManager Shell { get; init; }
    public required MediaService Media { get; init; }
    public required ClipboardHistory Clipboard { get; init; }
    public required AppIndex Apps { get; init; }
    /// <summary>Gauges, bars and the timer use this colour.</summary>
    public required Func<Brush> Accent { get; init; }
    public required Action<string> ShowTab { get; init; }
    public required Action Collapse { get; init; }
    /// <summary>Lets the notch take keyboard focus (it normally never does, so the app you're in keeps it).</summary>
    public required Action Activate { get; init; }
}

/// <summary>One page of the panel: Home's cards, or one of the tabs' bigger views.</summary>
abstract class NotchPage(NotchContext context)
{
    protected NotchContext Context { get; } = context;
    UIElement? _view;

    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string Glyph { get; }
    public UIElement View => _view ??= Build();

    protected abstract UIElement Build();
    /// <summary>The page is about to show.</summary>
    public virtual void Refresh() { }
    /// <summary>Every 1.5 seconds while showing.</summary>
    public virtual void Tick() { }

    protected TextBox Typing(TextBox box)
    {
        box.PreviewMouseLeftButtonDown += (_, _) => Context.Activate();
        box.GotKeyboardFocus += (_, _) => Context.Activate();
        return box;
    }

    public static readonly (string Id, string Title, string Glyph)[] All =
    [
        ("home", "Home", ""), ("call", "Voice call", ""), ("windows", "Windows", ""), ("system", "System", ""), ("clipboard", "Clipboard", ""), ("notes", "Notes", ""),
        ("focus", "Focus", ""), ("files", "Files", ""), ("apps", "Apps", ""), ("weather", "Weather", ""),
        ("media", "Music", ""), ("screentime", "Screen time", "")
    ];

    public static NotchPage Create(string id, NotchContext c) => id switch
    {
        "call" => new CallPage(c),
        "windows" => new WindowsPage(c),
        "system" => new SystemPage(c),
        "clipboard" => new ClipboardPage(c),
        "notes" => new NotesPage(c),
        "focus" => new FocusPage(c),
        "files" => new FilesPage(c),
        "apps" => new AppsPage(c),
        "weather" => new WeatherPage(c),
        "media" => new MediaPage(c),
        "screentime" => new ScreenTimePage(c),
        _ => new HomePage(c)
    };
}

// ---------------------------------------------------------------- Home

sealed class HomePage(NotchContext c) : NotchPage(c)
{
    public override string Id => "home";
    public override string Title => "Home";
    public override string Glyph => "";

    readonly List<Action> _refresh = new();
    readonly List<Action> _tick = new();

    protected override UIElement Build()
    {
        var grid = new Grid();
        for (var i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        Place(grid, ScreenTimeCard(), 0, 0);
        Place(grid, SystemCard(), 0, 1);
        Place(grid, WeatherCard(), 0, 2);
        Place(grid, FocusCard(), 1, 0);
        Place(grid, ClipboardCard(), 1, 1);
        Place(grid, NotesCard(), 1, 2);
        return grid;
    }

    static void Place(Grid grid, UIElement card, int row, int col)
    {
        if (card is FrameworkElement f) f.Margin = new Thickness(col == 0 ? 0 : 5, row == 0 ? 0 : 5, col == 2 ? 0 : 5, row == 1 ? 0 : 5);
        Grid.SetRow(card, row);
        Grid.SetColumn(card, col);
        grid.Children.Add(card);
    }

    public override void Refresh() { foreach (var r in _refresh) r(); }
    public override void Tick() { foreach (var t in _tick) t(); }

    UIElement ScreenTimeCard()
    {
        var total = UI.Label("", 27, bold: true, font: UI.Display);
        var icon = new Image { Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0) };
        var top = UI.Label("", 12, brush: UI.Dim);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        row.Children.Add(icon);
        row.Children.Add(top);
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        left.Children.Add(total);
        left.Children.Add(row);
        var bars = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        var body = new Grid { Children = { left, bars } };
        string? iconFor = null;

        void Update()
        {
            total.Text = ScreenTime.Format(ScreenTime.TodaySeconds);
            var best = ScreenTime.TopApps(1).FirstOrDefault();
            top.Text = best == null ? "Nothing yet today" : $"{best.Name} · {ScreenTime.Format(best.Seconds)}";
            if (best != null && best.Path != iconFor)
            {
                iconFor = best.Path;
                icon.Source = Files.IconLoader.Small(best.Path, false, null, img => icon.Source = img);
            }
            icon.Visibility = best == null ? Visibility.Collapsed : Visibility.Visible;
            HourBars(bars, 8, 22, 5, 9);
        }
        _refresh.Add(Update);
        return UI.CardBox("", "Screen time", body, () => Context.ShowTab("screentime"));
    }

    /// <summary>The last <paramref name="count"/> hours as small bars, the current one in colour.</summary>
    void HourBars(StackPanel bars, int count, double maxHeight, double width, double gap)
    {
        var hours = ScreenTime.Hours;
        var now = DateTime.Now.Hour;
        bars.Children.Clear();
        for (var i = count - 1; i >= 0; i--)
        {
            var h = (now - i + 24) % 24;
            var share = hours[h] / 3600.0;
            var bar = new Border
            {
                Width = width,
                Height = Math.Max(width, share * maxHeight),
                CornerRadius = new CornerRadius(width / 2),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(i == count - 1 ? 0 : gap, 0, 0, 0),
                Background = i == 0 ? Context.Accent() : UI.Faint
            };
            bars.Children.Add(bar);
        }
    }

    UIElement SystemCard()
    {
        var rings = new Grid();
        var stats = new StatsHolder();
        var cpu = RingWithLabel("CPU");
        var ram = RingWithLabel("RAM");
        var disk = RingWithLabel("DISK");
        for (var i = 0; i < 3; i++) rings.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(ram.Panel, 1);
        Grid.SetColumn(disk.Panel, 2);
        rings.Children.Add(cpu.Panel);
        rings.Children.Add(ram.Panel);
        rings.Children.Add(disk.Panel);

        void Update()
        {
            var s = stats.Sample();
            foreach (var r in new[] { cpu.Ring, ram.Ring, disk.Ring }) r.Fill = Context.Accent();
            cpu.Ring.Value = s.Cpu;
            ram.Ring.Value = s.Ram;
            disk.Ring.Value = s.Disk;
        }
        _refresh.Add(Update);
        _tick.Add(Update);
        return UI.CardBox("", "System", rings, () => Context.ShowTab("system"));
    }

    static (Ring Ring, StackPanel Panel) RingWithLabel(string label)
    {
        var ring = new Ring { Width = 50, Height = 50, Thickness = 4.5, TextSize = 11 };
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(ring);
        var text = UI.Label(label, 10, bold: true, brush: UI.Dim);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.Margin = new Thickness(0, 3, 0, 0);
        panel.Children.Add(text);
        return (ring, panel);
    }

    UIElement WeatherCard()
    {
        var big = UI.Label("", 15, bold: true);
        var small = UI.Label("", 12, brush: UI.Dim);
        var body = new StackPanel();
        body.Children.Add(big);
        body.Children.Add(small);

        async void Update()
        {
            if (string.IsNullOrWhiteSpace(App.Settings.WeatherCity))
            {
                big.FontSize = 15;
                big.Text = "No forecast yet";
                small.Text = "Set your city";
                return;
            }
            var (weather, problem) = await WeatherService.GetAsync(App.Settings.WeatherCity, App.Settings.Fahrenheit);
            if (weather == null)
            {
                big.FontSize = 15;
                big.Text = "No forecast yet";
                small.Text = problem ?? "";
                return;
            }
            big.FontSize = 27;
            big.FontFamily = UI.Display;
            big.Text = $"{Math.Round(weather.Temperature)}°  ";
            small.Text = $"{weather.Condition} · {weather.Place}";
        }
        _refresh.Add(Update);
        return UI.CardBox("", "Weather", body, () => Context.ShowTab("weather"));
    }

    UIElement FocusCard()
    {
        var time = UI.Label("", 27, bold: true, font: UI.Display);
        var status = UI.Label("", 12, brush: UI.Dim);
        var left = new StackPanel();
        left.Children.Add(time);
        left.Children.Add(status);
        var playGlyph = "";
        var play = UI.Round(playGlyph, 40, FocusTimer.Toggle, "Start or pause");
        play.HorizontalAlignment = HorizontalAlignment.Right;
        play.VerticalAlignment = VerticalAlignment.Bottom;
        var body = new Grid { Children = { left, play } };

        void Update()
        {
            time.Text = FocusTimer.Text;
            time.Foreground = FocusTimer.Running ? Context.Accent() : UI.Text;
            status.Text = FocusTimer.Status;
            ((TextBlock)((Border)play.Content).Child).Text = FocusTimer.Running ? "" : "";
        }
        FocusTimer.Changed += Update;
        _refresh.Add(Update);
        return UI.CardBox("", "Focus", body, () => Context.ShowTab("focus"));
    }

    UIElement ClipboardCard()
    {
        var list = new StackPanel();

        void Update()
        {
            list.Children.Clear();
            var items = Context.Clipboard.Items.Take(2).ToList();
            if (items.Count == 0)
            {
                list.Children.Add(UI.Label("Nothing copied yet", 12.5, bold: true));
                list.Children.Add(UI.Label("Copy some text and it shows here", 12, brush: UI.Dim));
                return;
            }
            for (var i = 0; i < items.Count; i++)
                list.Children.Add(ClipRow(Context.Clipboard, items[i], i == 0 ? UI.Ago(Context.Clipboard.LastCopied) : ""));
        }
        Context.Clipboard.Items.CollectionChanged += (_, _) => Update();
        _refresh.Add(Update);
        return UI.CardBox("", "Clipboard", list, () => Context.ShowTab("clipboard"));
    }

    internal static Grid ClipRow(ClipboardHistory clipboard, string text, string when)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var chip = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(6), Background = UI.Raised, Child = UI.Glyph("", 11, UI.Dim) };
        var label = UI.Label(text.ReplaceLineEndings(" ").Trim(), 12.5);
        label.Margin = new Thickness(9, 0, 6, 0);
        var ago = UI.Label(when, 11, brush: UI.Dim);
        var copy = UI.Round("", 24, () => clipboard.Restore(text), "Copy again");
        copy.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(label, 1);
        Grid.SetColumn(ago, 2);
        Grid.SetColumn(copy, 3);
        row.Children.Add(chip);
        row.Children.Add(label);
        row.Children.Add(ago);
        row.Children.Add(copy);
        return row;
    }

    UIElement NotesCard()
    {
        var latest = UI.Label("", 12.5, bold: true);
        var fieldHost = UI.Field("Write a note…", out var field);
        Typing(field);
        var add = UI.Round("", 28, () => AddNote(field), "Add note");
        add.Margin = new Thickness(8, 0, 0, 0);
        var entry = new DockPanel { VerticalAlignment = VerticalAlignment.Bottom };
        DockPanel.SetDock(add, Dock.Right);
        entry.Children.Add(add);
        entry.Children.Add(fieldHost);
        field.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            AddNote(field);
            e.Handled = true;
        };
        var body = new Grid { Children = { latest, entry } };
        latest.VerticalAlignment = VerticalAlignment.Top;

        void Update()
        {
            var note = NotesStore.Items.FirstOrDefault();
            latest.Text = note?.Text.ReplaceLineEndings(" ") ?? "No notes yet";
            latest.FontWeight = note == null ? FontWeights.SemiBold : FontWeights.Normal;
        }
        NotesStore.Items.CollectionChanged += (_, _) => Update();
        _refresh.Add(Update);
        return UI.CardBox("", "Notes", body, () => Context.ShowTab("notes"));
    }

    static void AddNote(TextBox field)
    {
        NotesStore.Add(field.Text);
        field.Clear();
    }
}

/// <summary>Reads CPU, memory and disk on demand; opened once and kept (a performance-counter query is cheap to keep).</summary>
sealed class StatsHolder
{
    static StatsService? _shared;
    public SystemStats Sample() => (_shared ??= new StatsService(withGpu: false)).Sample();
}

// ---------------------------------------------------------------- Voice call

/// <summary>Discord profile pictures with a ring while they talk and a badge when muted.</summary>
static class Avatars
{
    public static readonly Brush Talking = UI.Frozen(Color.FromRgb(0x3B, 0xD1, 0x6F));
    static readonly Brush Muted = UI.Frozen(Color.FromRgb(0xE0, 0x3C, 0x3C));
    static readonly Brush Blank = UI.Frozen(Color.FromRgb(0x2E, 0x2E, 0x2E));

    public static Grid Make(VoicePeer peer, double size, bool badge = true)
    {
        var ring = Math.Max(2, size / 18);
        var root = new Grid { Width = size, Height = size, ToolTip = peer.Name + (peer.Deafened ? " · deafened" : peer.Muted ? " · muted" : "") };
        var image = DiscordLink.Avatar(peer.Avatar);
        var inner = size - ring * 3;
        root.Children.Add(new System.Windows.Shapes.Ellipse { Width = inner, Height = inner, Fill = image != null ? new ImageBrush(image) { Stretch = Stretch.UniformToFill } : Blank });
        if (image == null && peer.Name.Length > 0)
        {
            var initial = UI.Label(peer.Name[..1].ToUpperInvariant(), size * 0.36, bold: true);
            initial.HorizontalAlignment = HorizontalAlignment.Center;
            root.Children.Add(initial);
        }
        if (peer.Speaking) root.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = Talking, StrokeThickness = ring });
        if (badge && (peer.Muted || peer.Deafened))
        {
            var b = size * 0.36;
            root.Children.Add(new Border
            {
                Width = b, Height = b, CornerRadius = new CornerRadius(b / 2), Background = Muted, BorderBrush = Brushes.Black, BorderThickness = new Thickness(1.5),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Child = UI.Glyph(peer.Deafened ? "" : "", b * 0.5)
            });
        }
        return root;
    }
}

sealed class CallPage(NotchContext c) : NotchPage(c)
{
    public override string Id => "call";
    public override string Title => "Voice call";
    public override string Glyph => "";

    readonly TextBlock _channel = UI.Label("", 18, bold: true, font: UI.Display);
    readonly TextBlock _server = UI.Label("", 12.5, brush: UI.Dim);
    readonly TextBlock _talking = UI.Label("", 12.5, brush: UI.Dim);
    readonly WrapPanel _people = new();
    bool _listening;

    protected override UIElement Build()
    {
        var head = new DockPanel { Margin = new Thickness(4, 0, 4, 12) };
        _talking.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(_talking, Dock.Right);
        head.Children.Add(_talking);
        var names = new StackPanel { Orientation = Orientation.Horizontal };
        names.Children.Add(UI.Glyph("", 16, Avatars.Talking));
        _channel.Margin = new Thickness(10, 0, 10, 0);
        names.Children.Add(_channel);
        names.Children.Add(_server);
        head.Children.Add(names);
        var page = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        page.Children.Add(head);
        page.Children.Add(new ScrollViewer { Content = _people, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return page;
    }

    public override void Refresh()
    {
        if (!_listening)
        {
            _listening = true;
            DiscordLink.CallChanged += () => { if (_people.IsVisible) Refresh(); };
        }
        var call = DiscordLink.Call;
        _people.Children.Clear();
        if (call == null)
        {
            _channel.Text = "Not in a voice call";
            _server.Text = _talking.Text = "";
            _people.Children.Add(UI.Label("Join a call in Discord and the people in it show up here (needs the AIChat plugin).", 12.5, brush: UI.Dim));
            return;
        }
        _channel.Text = call.Channel;
        _server.Text = call.Server ?? "";
        var talking = call.Peers.Where(p => p.Speaking).Select(p => p.Name).ToList();
        _talking.Text = talking.Count == 0 ? $"{call.Peers.Count} in the call" : $"{string.Join(", ", talking.Take(3))} talking";
        _talking.Foreground = talking.Count == 0 ? UI.Dim : Avatars.Talking;
        foreach (var peer in call.Peers)
        {
            var name = UI.Label(peer.Name, 12, brush: peer.Speaking ? UI.Text : UI.Dim);
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.MaxWidth = 92;
            name.Margin = new Thickness(0, 7, 0, 0);
            var cell = new StackPanel { Width = 100, Margin = new Thickness(0, 0, 4, 12) };
            var avatar = Avatars.Make(peer, 58);
            avatar.HorizontalAlignment = HorizontalAlignment.Center;
            cell.Children.Add(avatar);
            cell.Children.Add(name);
            _people.Children.Add(cell);
        }
    }
}

// ---------------------------------------------------------------- Windows

sealed class WindowsPage(NotchContext c) : NotchPage(c)
{
    public override string Id => "windows";
    public override string Title => "Windows";
    public override string Glyph => "";

    readonly WrapPanel _grid = new();
    System.ComponentModel.ICollectionView? _windows;
    string _shown = "";

    protected override UIElement Build()
    {
        _windows = Context.Shell.Tasks.CreateGroupedWindowsCollection();
        if (_windows != null) _windows.Filter = o => o is ManagedShell.WindowsTasks.ApplicationWindow { ShowInTaskbar: true };
        return new ScrollViewer { Content = _grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    public override void Refresh()
    {
        _shown = "";
        Tick();
    }

    /// <summary>Rebuilds only when windows open, close, change title or state.</summary>
    public override void Tick()
    {
        _windows?.Refresh();
        var list = _windows?.OfType<ManagedShell.WindowsTasks.ApplicationWindow>().ToList() ?? new();
        var signature = string.Join("|", list.Select(w => $"{w.Handle}:{w.Title}:{w.State}:{w.IsMinimized}"));
        if (signature == _shown) return;
        _shown = signature;
        _grid.Children.Clear();
        foreach (var window in list) _grid.Children.Add(Tile(window));
        if (list.Count == 0) _grid.Children.Add(UI.Label("No open windows", 13, brush: UI.Dim));
    }

    Button Tile(ManagedShell.WindowsTasks.ApplicationWindow window)
    {
        var active = window.State == ManagedShell.WindowsTasks.ApplicationWindow.WindowState.Active;
        var flashing = window.State == ManagedShell.WindowsTasks.ApplicationWindow.WindowState.Flashing;
        var icon = new Image { Width = 28, Height = 28, Opacity = window.IsMinimized ? 0.5 : 1, VerticalAlignment = VerticalAlignment.Center };
        // Windows' icons load in the background; follow the window's Icon as it arrives.
        icon.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(window.Icon)) { Source = window });
        var title = new TextBlock
        {
            Text = window.Title, Foreground = window.IsMinimized ? UI.Dim : UI.Text, FontFamily = UI.Font, FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 34, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(11, 0, 0, 0)
        };
        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(title);
        var card = new Border
        {
            Width = 228, Height = 62, Margin = new Thickness(0, 0, 9, 9), Padding = new Thickness(14, 8, 12, 8), CornerRadius = new CornerRadius(14),
            Background = UI.Card, BorderThickness = new Thickness(active || flashing ? 1.5 : 1),
            BorderBrush = active ? Context.Accent() : flashing ? UI.Frozen(Color.FromRgb(0xF7, 0xA3, 0x25)) : UI.CardEdge,
            Child = row
        };
        var button = new Button { Content = card, Template = UI.Bare(), Cursor = Cursors.Hand, Focusable = false, ToolTip = window.Title };
        System.Windows.Automation.AutomationProperties.SetName(button, window.Title);
        button.MouseEnter += (_, _) => card.Background = UI.Raised;
        button.MouseLeave += (_, _) => card.Background = UI.Card;
        button.Click += (_, _) =>
        {
            Context.Collapse();
            window.BringToFront();
        };
        button.MouseUp += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            window.Close(); // middle-click closes, like a browser tab
            e.Handled = true;
        };
        var menu = new ContextMenu();
        void Add(string header, Action run)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => run();
            menu.Items.Add(item);
        }
        Add("Bring to front", () => { Context.Collapse(); window.BringToFront(); });
        if (window.CanMinimize) Add("Minimise", window.Minimize);
        Add("Pin", () => DockPins.PinWindow(window));
        Add("Close window", window.Close);
        button.ContextMenu = menu;
        return button;
    }
}

// ---------------------------------------------------------------- System

sealed class SystemPage(NotchContext c) : NotchPage(c)
{
    public override string Id => "system";
    public override string Title => "System";
    public override string Glyph => "";

    readonly StatsHolder _stats = new();
    readonly Ring[] _rings = new Ring[3];
    readonly TextBlock _memory = UI.Label("", 13);
    readonly TextBlock _uptime = UI.Label("", 13, brush: UI.Dim);

    protected override UIElement Build()
    {
        var rings = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
        string[] names = ["CPU", "Memory", "Disk"];
        for (var i = 0; i < 3; i++)
        {
            _rings[i] = new Ring { Width = 110, Height = 110, Thickness = 8, TextSize = 20 };
            var col = new StackPanel { Margin = new Thickness(i == 0 ? 0 : 54, 0, 0, 0) };
            col.Children.Add(_rings[i]);
            var label = UI.Label(names[i], 12, bold: true, brush: UI.Dim);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.Margin = new Thickness(0, 8, 0, 0);
            col.Children.Add(label);
            rings.Children.Add(col);
        }
        var info = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0), Orientation = Orientation.Horizontal };
        info.Children.Add(_memory);
        _uptime.Margin = new Thickness(18, 0, 18, 0);
        info.Children.Add(_uptime);
        info.Children.Add(UI.Pill("Task Manager", () => { Context.Collapse(); App.Instance.OpenTaskManager(); }, ""));
        info.Children.Add(UI.Pill("Windows Settings", () => { Context.Collapse(); ToolsMenu.WindowsSettings(); }, ""));
        var page = new StackPanel();
        page.Children.Add(rings);
        page.Children.Add(info);
        return page;
    }

    public override void Refresh() => Tick();

    public override void Tick()
    {
        var s = _stats.Sample();
        double[] values = [s.Cpu, s.Ram, s.Disk];
        for (var i = 0; i < 3; i++)
        {
            _rings[i].Fill = Context.Accent();
            _rings[i].Value = values[i];
        }
        _memory.Text = $"Memory {s.RamUsedGb:0.0} of {s.RamTotalGb:0} GB";
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        _uptime.Text = up.TotalDays >= 1 ? $"Up {(int)up.TotalDays}d {up.Hours}h" : $"Up {up.Hours}h {up.Minutes}m";
    }
}

// ---------------------------------------------------------------- Clipboard

sealed class ClipboardPage(NotchContext c) : NotchPage(c)
{
    public override string Id => "clipboard";
    public override string Title => "Clipboard";
    public override string Glyph => "";

    readonly StackPanel _list = new();

    protected override UIElement Build()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        bar.Children.Add(UI.Label("Copied text, newest first. Click to copy it again.", 12.5, brush: UI.Dim));
        var clear = UI.Pill("Clear", () => Context.Clipboard.Clear(), "");
        clear.Margin = new Thickness(16, 0, 0, 0);
        bar.Children.Add(clear);
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Context.Clipboard.Items.CollectionChanged += (_, _) => Refresh();
        return dock;
    }

    public override void Refresh()
    {
        _list.Children.Clear();
        foreach (var text in Context.Clipboard.Items)
        {
            var row = HomePage.ClipRow(Context.Clipboard, text, "");
            row.Margin = new Thickness(0, 0, 8, 7);
            _list.Children.Add(row);
        }
        if (_list.Children.Count == 0) _list.Children.Add(UI.Label("Nothing copied yet", 13, brush: UI.Dim));
    }
}

// ---------------------------------------------------------------- Notes

sealed class NotesPage(NotchContext c) : NotchPage(c)
{
    public override string Id => "notes";
    public override string Title => "Notes";
    public override string Glyph => "";

    readonly WrapPanel _list = new();

    protected override UIElement Build()
    {
        var fieldHost = UI.Field("Write a note and press Enter…", out var field);
        Typing(field);
        field.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            NotesStore.Add(field.Text);
            field.Clear();
            e.Handled = true;
        };
        fieldHost.Margin = new Thickness(0, 0, 0, 10);
        var dock = new DockPanel();
        DockPanel.SetDock(fieldHost, Dock.Top);
        dock.Children.Add(fieldHost);
        dock.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        NotesStore.Items.CollectionChanged += (_, _) => Refresh();
        return dock;
    }

    public override void Refresh()
    {
        _list.Children.Clear();
        foreach (var note in NotesStore.Items)
        {
            var text = new TextBlock { Text = note.Text, Foreground = UI.Text, FontFamily = UI.Font, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxHeight = 60, TextTrimming = TextTrimming.CharacterEllipsis };
            var when = UI.Label(note.Created.ToString("d MMM, HH:mm"), 11, brush: UI.Dim);
            var remove = UI.Round("", 22, () => NotesStore.Remove(note), "Delete note");
            remove.HorizontalAlignment = HorizontalAlignment.Right;
            var foot = new Grid { Margin = new Thickness(0, 6, 0, 0), Children = { when, remove } };
            var stack = new DockPanel();
            DockPanel.SetDock(foot, Dock.Bottom);
            stack.Children.Add(foot);
            stack.Children.Add(text);
            _list.Children.Add(new Border
            {
                Width = 232, Height = 104, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(12, 10, 10, 8),
                CornerRadius = new CornerRadius(14), Background = UI.Card, BorderBrush = UI.CardEdge, BorderThickness = new Thickness(1), Child = stack
            });
        }
        if (_list.Children.Count == 0) _list.Children.Add(UI.Label("No notes yet", 13, brush: UI.Dim));
    }
}

// ---------------------------------------------------------------- Focus

sealed class FocusPage(NotchContext c) : NotchPage(c)
{
    public override string Id => "focus";
    public override string Title => "Focus";
    public override string Glyph => "";

    readonly TextBlock _time = UI.Label("", 64, bold: true, font: UI.Display);
    readonly TextBlock _status = UI.Label("", 13, brush: UI.Dim);
    Button? _play;
    readonly StackPanel _lengths = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };

    protected override UIElement Build()
    {
        _time.HorizontalAlignment = _status.HorizontalAlignment = HorizontalAlignment.Center;
        _play = UI.Round("", 52, FocusTimer.Toggle, "Start or pause", light: true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        var reset = UI.Round("", 40, FocusTimer.Reset, "Reset");
        var skip = UI.Round("", 40, FocusTimer.Skip, "Skip to the next part");
        reset.Margin = new Thickness(0, 6, 14, 0);
        skip.Margin = new Thickness(14, 6, 0, 0);
        buttons.Children.Add(reset);
        buttons.Children.Add(_play);
        buttons.Children.Add(skip);
        foreach (var minutes in new[] { 15, 25, 45, 60 })
            _lengths.Children.Add(UI.Pill($"{minutes} min", () => FocusTimer.SetMinutes(minutes)));
        var page = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        page.Children.Add(_time);
        page.Children.Add(_status);
        page.Children.Add(buttons);
        page.Children.Add(_lengths);
        FocusTimer.Changed += Refresh;
        return page;
    }

    public override void Refresh()
    {
        _time.Text = FocusTimer.Text;
        _time.Foreground = FocusTimer.Running ? Context.Accent() : UI.Text;
        _status.Text = FocusTimer.Status;
        if (_play != null) ((TextBlock)((Border)_play.Content).Child).Text = FocusTimer.Running ? "" : "";
        _lengths.Visibility = FocusTimer.Current == FocusTimer.Phase.Ready && !FocusTimer.Running ? Visibility.Visible : Visibility.Hidden;
    }
}

// ---------------------------------------------------------------- Files

sealed class FilesPage(NotchContext c) : NotchPage(c)
{
    public override string Id => "files";
    public override string Title => "Files";
    public override string Glyph => "";

    readonly StackPanel _recent = new();

    protected override UIElement Build()
    {
        var places = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        (string Name, string Glyph, string Path)[] list =
        [
            ("Home", "", ""), ("Desktop", "", Files.FileView.Desktop), ("Downloads", "", Files.FileView.Downloads),
            ("Documents", "", Files.FileView.Documents), ("Pictures", "", Files.FileView.Pictures), ("Music", "", Files.FileView.Music),
            ("Videos", "", Files.FileView.Videos)
        ];
        foreach (var place in list)
        {
            var path = place.Path;
            places.Children.Add(UI.Pill(place.Name, () => Open(path.Length == 0 ? null : path), place.Glyph));
        }
        var head = UI.Label("RECENT", 11, bold: true, brush: UI.Dim);
        head.Margin = new Thickness(2, 0, 0, 6);
        var page = new DockPanel();
        DockPanel.SetDock(places, Dock.Top);
        DockPanel.SetDock(head, Dock.Top);
        page.Children.Add(places);
        page.Children.Add(head);
        page.Children.Add(new ScrollViewer { Content = _recent, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return page;
    }

    void Open(string? folder, string? select = null)
    {
        Context.Collapse();
        App.Instance.OpenFolder(folder, select);
    }

    public override async void Refresh()
    {
        var items = await Task.Run(RecentFiles);
        _recent.Children.Clear();
        foreach (var path in items)
        {
            var icon = new Image { Width = 18, Height = 18, Margin = new Thickness(0, 0, 10, 0) };
            var ext = Path.GetExtension(path).ToLowerInvariant();
            // Programs and shortcuts have their own icons; other files share their type's.
            var key = Directory.Exists(path) ? "<folder>" : ext is ".exe" or ".lnk" or ".url" or ".ico" or "" ? null : ext;
            icon.Source = Files.IconLoader.Small(path, Directory.Exists(path), key, img => icon.Source = img);
            var name = UI.Label(Path.GetFileName(path), 12.5);
            var where = UI.Label(Path.GetDirectoryName(path) ?? "", 11.5, brush: UI.Dim);
            where.Margin = new Thickness(12, 0, 0, 0);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, name, where } };
            var target = path;
            var button = new Button { Content = new Border { Padding = new Thickness(6, 5, 6, 5), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Child = row },
                Template = UI.Bare(), Cursor = Cursors.Hand, Focusable = false, ToolTip = path };
            button.MouseEnter += (_, _) => ((Border)button.Content).Background = UI.Raised;
            button.MouseLeave += (_, _) => ((Border)button.Content).Background = Brushes.Transparent;
            button.Click += (_, _) =>
            {
                Context.Collapse();
                Files.FileOps.Open(target);
            };
            button.MouseRightButtonUp += (_, e) =>
            {
                Open(Path.GetDirectoryName(target), target);
                e.Handled = true;
            };
            _recent.Children.Add(button);
        }
        if (_recent.Children.Count == 0) _recent.Children.Add(UI.Label("No recent files", 12.5, brush: UI.Dim));
    }

    /// <summary>Files Windows lists as recently opened (its Recent folder's shortcuts), newest first.</summary>
    static List<string> RecentFiles()
    {
        var result = new List<string>();
        try
        {
            var folder = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            foreach (var lnk in new DirectoryInfo(folder).EnumerateFiles("*.lnk").OrderByDescending(f => f.LastWriteTimeUtc).Take(40))
            {
                var target = Files.ShellNative.LinkTarget(lnk.FullName);
                if (target == null || !File.Exists(target) || result.Contains(target, StringComparer.OrdinalIgnoreCase)) continue;
                result.Add(target);
                if (result.Count == 8) break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't list recent files", ex);
        }
        if (result.Count > 0) return result;

        // Windows isn't keeping a recent list (it can be turned off): the newest files in the usual places instead.
        try
        {
            return new[] { Files.FileView.Downloads, Files.FileView.Desktop, Files.FileView.Documents }
                .Where(Directory.Exists)
                .SelectMany(d => new DirectoryInfo(d).EnumerateFiles())
                .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0 && !f.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(8)
                .Select(f => f.FullName)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't list new files", ex);
            return result;
        }
    }
}

// ---------------------------------------------------------------- Apps

sealed class AppsPage(NotchContext c) : NotchPage(c)
{
    public override string Id => "apps";
    public override string Title => "Apps";
    public override string Glyph => "";

    readonly WrapPanel _grid = new();
    TextBox? _search;

    protected override UIElement Build()
    {
        var fieldHost = UI.Field("Search apps…", out var field);
        _search = Typing(field);
        field.TextChanged += (_, _) => Fill();
        field.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            if (_grid.Children.OfType<Button>().FirstOrDefault() is { } first) first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
        };
        var all = UI.Pill("All apps", () => { Context.Collapse(); App.Instance.ToggleAppDrawer(); }, "");
        all.Margin = new Thickness(10, 0, 0, 0);
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(all, Dock.Right);
        top.Children.Add(all);
        top.Children.Add(fieldHost);
        var page = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        page.Children.Add(top);
        page.Children.Add(new ScrollViewer { Content = _grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return page;
    }

    public override void Refresh()
    {
        if (_search != null) _search.Text = "";
        Fill();
    }

    void Fill()
    {
        _grid.Children.Clear();
        var query = _search?.Text ?? "";
        IEnumerable<(string Name, string Target)> apps;
        if (query.Trim().Length > 0) apps = Context.Apps.SearchNames(query, 14);
        else
        {
            var all = Context.Apps.AllApps().ToDictionary(a => a.Target, a => a.Name, StringComparer.OrdinalIgnoreCase);
            apps = App.Settings.RecentApps.Where(all.ContainsKey).Select(t => (all[t], t))
                .Concat(DockPins.Items.Where(p => p.Pin.Kind == "app" && all.ContainsKey(p.Pin.Target)).Select(p => (p.Pin.Name, p.Pin.Target)))
                .DistinctBy(a => a.Item2, StringComparer.OrdinalIgnoreCase).Take(14);
        }
        foreach (var (name, target) in apps)
        {
            var icon = new Image { Width = 34, Height = 34, Source = Context.Apps.IconFor(target) };
            var label = UI.Label(name, 11.5);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.MaxWidth = 84;
            label.Margin = new Thickness(0, 6, 0, 0);
            var cell = new StackPanel { Children = { icon, label } };
            var bg = new Border { Width = 96, Height = 82, CornerRadius = new CornerRadius(14), Padding = new Thickness(6, 10, 6, 6), Background = Brushes.Transparent, Child = cell };
            var button = new Button { Content = bg, Template = UI.Bare(), Cursor = Cursors.Hand, Focusable = false, ToolTip = name };
            button.MouseEnter += (_, _) => bg.Background = UI.Raised;
            button.MouseLeave += (_, _) => bg.Background = Brushes.Transparent;
            button.Click += (_, _) =>
            {
                Context.Collapse();
                App.Settings.RecordRecentApp(target);
                AppIndex.Launch(target);
            };
            _grid.Children.Add(button);
        }
        if (_grid.Children.Count == 0)
            _grid.Children.Add(UI.Label(query.Length > 0 ? "No apps match that" : "Apps you open show up here", 12.5, brush: UI.Dim));
    }
}

// ---------------------------------------------------------------- Weather

sealed class WeatherPage(NotchContext c) : NotchPage(c)
{
    public override string Id => "weather";
    public override string Title => "Weather";
    public override string Glyph => "";

    readonly TextBlock _icon = UI.Glyph("", 64);
    readonly TextBlock _temp = UI.Label("", 54, bold: true, font: UI.Display);
    readonly TextBlock _condition = UI.Label("", 15);
    readonly TextBlock _range = UI.Label("", 13, brush: UI.Dim);
    TextBox? _city;

    protected override UIElement Build()
    {
        var now = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0) };
        _icon.Margin = new Thickness(0, 0, 22, 0);
        now.Children.Add(_icon);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(_temp);
        words.Children.Add(_condition);
        words.Children.Add(_range);
        now.Children.Add(words);

        var fieldHost = UI.Field("Your city", out var field);
        _city = Typing(field);
        fieldHost.Width = 220;
        var save = UI.Pill("Save", Save, "");
        save.Margin = new Thickness(8, 0, 0, 0);
        field.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Save(); e.Handled = true; } };
        var cityRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0) };
        cityRow.Children.Add(fieldHost);
        cityRow.Children.Add(save);
        var page = new StackPanel();
        page.Children.Add(now);
        page.Children.Add(cityRow);
        return page;
    }

    void Save()
    {
        if (_city == null) return;
        App.Settings.WeatherCity = _city.Text.Trim();
        App.Settings.Save();
        Refresh();
    }

    public override async void Refresh()
    {
        if (_city != null && !_city.IsKeyboardFocused) _city.Text = App.Settings.WeatherCity;
        if (string.IsNullOrWhiteSpace(App.Settings.WeatherCity))
        {
            _icon.Text = "";
            _temp.Text = "";
            _condition.Text = "No forecast yet";
            _range.Text = "Type your city below (only the city name is sent, to Open-Meteo)";
            return;
        }
        var (weather, problem) = await WeatherService.GetAsync(App.Settings.WeatherCity, App.Settings.Fahrenheit);
        if (weather == null)
        {
            _temp.Text = "";
            _condition.Text = problem ?? "Weather unavailable";
            _range.Text = "";
            return;
        }
        _icon.Text = weather.Glyph;
        _temp.Text = $"{Math.Round(weather.Temperature)}°";
        _condition.Text = $"{weather.Condition} · {weather.Place}";
        _range.Text = $"High {Math.Round(weather.High)}°  ·  Low {Math.Round(weather.Low)}°";
    }
}

// ---------------------------------------------------------------- Music

sealed class MediaPage(NotchContext c) : NotchPage(c)
{
    public override string Id => "media";
    public override string Title => "Music";
    public override string Glyph => "";

    readonly Border _art = new() { Width = 132, Height = 132, CornerRadius = new CornerRadius(18), Background = UI.Raised };
    readonly TextBlock _title = UI.Label("", 20, bold: true, font: UI.Display);
    readonly TextBlock _artist = UI.Label("", 14, brush: UI.Dim);
    Button? _play;
    bool _listening;

    protected override UIElement Build()
    {
        _play = UI.Round("", 50, () => _ = Context.Media.PlayPauseAsync(), "Play or pause", light: true);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
        var prev = UI.Round("", 40, () => _ = Context.Media.PreviousAsync(), "Previous");
        var next = UI.Round("", 40, () => _ = Context.Media.NextAsync(), "Next");
        prev.Margin = new Thickness(0, 5, 12, 0);
        next.Margin = new Thickness(12, 5, 0, 0);
        controls.Children.Add(prev);
        controls.Children.Add(_play);
        controls.Children.Add(next);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(26, 0, 0, 0), MaxWidth = 440 };
        words.Children.Add(_title);
        words.Children.Add(_artist);
        words.Children.Add(controls);
        var page = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        page.Children.Add(_art);
        page.Children.Add(words);
        return page;
    }

    public override async void Refresh()
    {
        if (!_listening)
        {
            _listening = true;
            Context.Media.Changed += () => Application.Current.Dispatcher.BeginInvoke(Refresh);
        }
        var now = await Context.Media.GetAsync();
        _title.Text = now?.Title ?? "Nothing playing";
        _artist.Text = now?.Artist ?? "Play something in Spotify, a browser or any media app";
        _art.Child = now?.Art != null ? new Image { Source = now.Art, Stretch = Stretch.UniformToFill, Clip = new RectangleGeometry(new Rect(0, 0, 132, 132), 18, 18) }
            : UI.Glyph("", 40, UI.Faint);
        if (_play != null) ((TextBlock)((Border)_play.Content).Child).Text = now?.IsPlaying == true ? "" : "";
    }
}

// ---------------------------------------------------------------- Screen time

sealed class ScreenTimePage(NotchContext c) : NotchPage(c)
{
    public override string Id => "screentime";
    public override string Title => "Screen time";
    public override string Glyph => "";

    readonly TextBlock _total = UI.Label("", 34, bold: true, font: UI.Display);
    readonly StackPanel _hours = new() { Orientation = Orientation.Horizontal, Height = 120, VerticalAlignment = VerticalAlignment.Bottom };
    readonly StackPanel _apps = new();

    protected override UIElement Build()
    {
        var left = new StackPanel { Width = 360 };
        left.Children.Add(UI.Label("TODAY", 11, bold: true, brush: UI.Dim));
        left.Children.Add(_total);
        _hours.Margin = new Thickness(0, 14, 0, 4);
        left.Children.Add(_hours);
        var axis = new Grid();
        foreach (var (text, align) in new[] { ("0:00", HorizontalAlignment.Left), ("12:00", HorizontalAlignment.Center), ("23:00", HorizontalAlignment.Right) })
        {
            var t = UI.Label(text, 10.5, brush: UI.Faint);
            t.HorizontalAlignment = align;
            axis.Children.Add(t);
        }
        left.Children.Add(axis);
        _apps.Margin = new Thickness(36, 0, 0, 0);
        var page = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        page.Children.Add(left);
        page.Children.Add(_apps);
        return page;
    }

    public override void Refresh()
    {
        _total.Text = ScreenTime.Format(ScreenTime.TodaySeconds);
        var hours = ScreenTime.Hours;
        _hours.Children.Clear();
        for (var h = 0; h < 24; h++)
            _hours.Children.Add(new Border
            {
                Width = 9, Margin = new Thickness(h == 0 ? 0 : 6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom,
                Height = Math.Max(4, hours[h] / 3600.0 * 110), CornerRadius = new CornerRadius(3),
                Background = h == DateTime.Now.Hour ? Context.Accent() : hours[h] > 0 ? UI.Dim : UI.Raised,
                ToolTip = $"{h}:00 · {ScreenTime.Format(hours[h])}"
            });

        _apps.Children.Clear();
        _apps.Children.Add(new TextBlock { Text = "MOST USED", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = UI.Dim, Margin = new Thickness(0, 0, 0, 8) });
        var top = ScreenTime.TopApps(6);
        var max = Math.Max(1, top.FirstOrDefault()?.Seconds ?? 1);
        foreach (var app in top)
        {
            var icon = new Image { Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) };
            icon.Source = Files.IconLoader.Small(app.Path, false, null, img => icon.Source = img);
            var name = UI.Label(app.Name, 12.5);
            var time = UI.Label(ScreenTime.Format(app.Seconds), 12, brush: UI.Dim);
            time.HorizontalAlignment = HorizontalAlignment.Right;
            var head = new Grid { Children = { new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, name } }, time } };
            var track = new Grid { Height = 4, Margin = new Thickness(24, 4, 0, 9) };
            track.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = UI.Raised });
            track.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = Context.Accent(), HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Max(4, 290.0 * app.Seconds / max) });
            _apps.Children.Add(head);
            _apps.Children.Add(track);
        }
        if (top.Count == 0) _apps.Children.Add(UI.Label("Nothing counted yet today", 12.5, brush: UI.Dim));
    }

    public override void Tick() { }
}
