using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskShell.Services;
using ManagedShell.Common.Helpers;

namespace DeskShell.Notch;

/// <summary>
/// The notch's Notifications tab: Windows' recent notifications and unread Discord DMs and mentions,
/// newest first, with the apps they came from on the left to narrow the list down. Windows doesn't let
/// other apps dismiss its notifications, so Clear and Hide only take them off this list.
/// </summary>
sealed class NotificationsPage(NotchContext c) : NotchPage(c)
{
    public override string Id => "notifications";
    public override string Title => "Notifications";
    public override string Glyph => "";

    readonly TextBlock _count = UI.Label("", 22, bold: true, font: UI.Display);
    readonly TextBlock _latest = UI.Label("", 12, brush: UI.Dim);
    readonly WrapPanel _actions = new() { Margin = new Thickness(0, 10, 0, 6) };
    readonly StackPanel _apps = new();
    readonly StackPanel _list = new();
    // Hidden one by one (this session only); Clear hides everything up to a moment, and that's kept.
    static readonly HashSet<string> Hidden = [];
    string? _filter;
    bool _listening;
    int _loading;

    sealed record Item(string Key, string Source, string App, ImageSource? Icon, bool Round, string Title, string Body, DateTime Arrived, Action Open);

    protected override UIElement Build()
    {
        var left = new DockPanel { Width = 230, Margin = new Thickness(0, 0, 14, 0) };
        var head = new StackPanel();
        head.Children.Add(_count);
        head.Children.Add(_latest);
        head.Children.Add(_actions);
        DockPanel.SetDock(head, Dock.Top);
        left.Children.Add(head);
        left.Children.Add(new ScrollViewer { Content = _apps, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var page = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        page.Children.Add(left);
        page.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return page;
    }

    public override void Refresh()
    {
        if (!_listening)
        {
            _listening = true;
            NotificationWatcher.Arrived += _ => { if (_list.IsVisible) Refresh(); };
            DiscordLink.MessagesChanged += () => { if (_list.IsVisible) Refresh(); };
        }
        _ = LoadAsync();
    }

    async Task LoadAsync()
    {
        var mine = ++_loading;
        var toasts = await NotificationReader.ReadAsync(60);
        if (mine != _loading) return; // a newer look started meanwhile

        var cleared = App.Settings.Notch.NotificationsCleared;
        var items = new List<Item>();
        if (App.Settings.Island.DiscordMessages)
            foreach (var m in DiscordLink.Messages)
            {
                var message = m;
                items.Add(new Item("discord:" + m.Id, "discord", "Discord", DiscordLink.Avatar(m.Avatar), true, m.Title, m.Text, m.Arrived,
                    () => DiscordLink.Open(message)));
            }
        foreach (var t in toasts)
        {
            var app = Context.Apps.Find(t.AppId);
            var target = app?.Target;
            items.Add(new Item($"{t.AppId}|{t.Arrived.Ticks}|{t.Title}", t.AppId, app?.Name ?? FallbackName(t.AppId), app?.Icon, false, t.Title, t.Body, t.Arrived,
                () =>
                {
                    if (target != null) AppIndex.Launch(target);
                    else ShellHelper.ShowNotificationCenter();
                }));
        }
        items = items.Where(i => i.Arrived > cleared && !Hidden.Contains(i.Key)).OrderByDescending(i => i.Arrived).ToList();
        Draw(items);
    }

    /// <summary>A readable name for an app Windows knows only by its id ("Microsoft.Windows.Explorer" → "Explorer").</summary>
    static string FallbackName(string appId)
    {
        var id = appId.Split('!')[0];
        var last = id.Split('.', '_', '\\').LastOrDefault(p => p.Length > 0) ?? id;
        return last.Length > 0 ? last : "Windows";
    }

    void Draw(List<Item> items)
    {
        if (_filter != null && items.All(i => i.Source != _filter)) _filter = null;

        _count.Text = items.Count switch { 0 => "All caught up", 1 => "1 notification", var n => $"{n} notifications" };
        _latest.Text = items.Count == 0 ? "Nothing new" : $"Latest {Ago(items[0].Arrived)}";

        _actions.Children.Clear();
        if (DoNotDisturb.IsSupported)
        {
            var on = DoNotDisturb.IsOn;
            _actions.Children.Add(Chip(on ? "Do not disturb on" : "Do not disturb", "", on, () =>
            {
                DoNotDisturb.SetOn(!on);
                Refresh();
            }));
        }
        if (items.Count > 0)
            _actions.Children.Add(Chip("Clear", "", false, () =>
            {
                App.Settings.Notch.NotificationsCleared = DateTime.Now;
                App.Settings.Save();
                Hidden.Clear();
                Refresh();
            }, "Take everything off this list (Windows keeps its own copies)"));
        _actions.Children.Add(Chip("Windows' list", "", false, () =>
        {
            Context.Collapse();
            ShellHelper.ShowNotificationCenter();
        }, "Open Windows' notification centre"));

        _apps.Children.Clear();
        if (items.Count > 0)
        {
            _apps.Children.Add(AppRow(null, "All", null, false, items.Count));
            foreach (var group in items.GroupBy(i => i.Source))
            {
                var first = group.First();
                _apps.Children.Add(AppRow(group.Key, first.App, first.Source == "discord" ? null : first.Icon, first.Source == "discord", group.Count()));
            }
        }

        _list.Children.Clear();
        foreach (var item in _filter == null ? items : items.Where(i => i.Source == _filter))
            _list.Children.Add(Row(item));
        if (_list.Children.Count == 0)
        {
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 40, 0, 0) };
            var bell = UI.Glyph("", 34, UI.Faint);
            bell.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Children.Add(bell);
            var words = UI.Label("No notifications", 13, brush: UI.Dim);
            words.HorizontalAlignment = HorizontalAlignment.Center;
            words.Margin = new Thickness(0, 10, 0, 0);
            empty.Children.Add(words);
            _list.Children.Add(empty);
        }
    }

    static string Ago(DateTime when) => UI.Ago(when) is var a && a == "now" ? "just now" : a + " ago";

    /// <summary>One app in the left column; click to show only its notifications.</summary>
    UIElement AppRow(string? source, string name, ImageSource? icon, bool discord, int count)
    {
        var selected = _filter == source;
        FrameworkElement picture = icon != null
            ? new Image { Source = icon, Width = 18, Height = 18 }
            : UI.Glyph(source == null ? "" : discord ? "" : "", 13, UI.Dim);
        picture.Margin = new Thickness(0, 0, 10, 0);
        picture.VerticalAlignment = VerticalAlignment.Center;
        var label = UI.Label(name, 12.5, bold: selected);
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        var number = UI.Label(count.ToString(), 11.5, brush: UI.Dim);
        number.Margin = new Thickness(8, 0, 0, 0);
        var row = new DockPanel();
        DockPanel.SetDock(picture, Dock.Left);
        DockPanel.SetDock(number, Dock.Right);
        row.Children.Add(picture);
        row.Children.Add(number);
        row.Children.Add(label);
        var bg = new Border
        {
            Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 4, 2),
            Background = selected ? UI.Raised : Brushes.Transparent, Child = row
        };
        var button = new Button { Content = bg, Template = UI.Bare(), Cursor = Cursors.Hand, Focusable = false };
        System.Windows.Automation.AutomationProperties.SetName(button, $"{name}, {count}");
        button.MouseEnter += (_, _) => bg.Background = UI.Raised;
        button.MouseLeave += (_, _) => bg.Background = selected ? UI.Raised : Brushes.Transparent;
        button.Click += (_, _) =>
        {
            _filter = source;
            Refresh();
        };
        return button;
    }

    /// <summary>One notification: click opens it (Discord at the message, other apps by starting them).</summary>
    UIElement Row(Item item)
    {
        var size = 30.0;
        FrameworkElement picture = item.Icon != null
            ? new Image { Source = item.Icon, Width = size, Height = size, Stretch = Stretch.UniformToFill, Clip = item.Round ? new EllipseGeometry(new Point(size / 2, size / 2), size / 2, size / 2) : null }
            : new Border { Width = size, Height = size, CornerRadius = new CornerRadius(item.Round ? size / 2 : 8), Background = UI.Raised, Child = UI.Glyph("", 13, UI.Dim) };
        picture.VerticalAlignment = VerticalAlignment.Top;
        picture.Margin = new Thickness(0, 2, 12, 0);

        var app = UI.Label(item.App, 11, bold: true, brush: UI.Dim);
        var when = UI.Label(UI.Ago(item.Arrived), 11, brush: UI.Dim);
        DockPanel.SetDock(when, Dock.Right);
        var top = new DockPanel { Children = { when, app } };
        var title = new TextBlock
        {
            Text = item.Title, Foreground = UI.Text, FontFamily = UI.Font, FontSize = 13, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0)
        };
        var words = new StackPanel();
        words.Children.Add(top);
        words.Children.Add(title);
        if (item.Body.Length > 0)
            words.Children.Add(new TextBlock
            {
                Text = item.Body.ReplaceLineEndings(" "), Foreground = UI.Dim, FontFamily = UI.Font, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                MaxHeight = 34, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0)
            });

        var content = new DockPanel();
        DockPanel.SetDock(picture, Dock.Left);
        content.Children.Add(picture);
        content.Children.Add(words);
        var card = new Border
        {
            Background = UI.Card, BorderBrush = UI.CardEdge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 0, 6, 7), Child = content
        };
        var button = new Button { Content = card, Template = UI.Bare(), Cursor = Cursors.Hand, Focusable = false, ToolTip = item.Body.Length > 0 ? $"{item.Title}\n{item.Body}" : item.Title };
        System.Windows.Automation.AutomationProperties.SetName(button, $"{item.App}: {item.Title}");
        button.MouseEnter += (_, _) => card.Background = UI.Raised;
        button.MouseLeave += (_, _) => card.Background = UI.Card;
        button.Click += (_, _) =>
        {
            Context.Collapse();
            item.Open();
        };
        button.MouseUp += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            Hide(item); // middle-click hides it, like closing a tab
            e.Handled = true;
        };

        var menu = new ContextMenu();
        void Add(string header, Action run)
        {
            var entry = new MenuItem { Header = header };
            entry.Click += (_, _) => run();
            menu.Items.Add(entry);
        }
        Add(item.Source == "discord" ? "Open in Discord" : "Open the app", () => { Context.Collapse(); item.Open(); });
        Add("Hide", () => Hide(item));
        Add("Open Windows' notification centre", () => { Context.Collapse(); ShellHelper.ShowNotificationCenter(); });
        button.ContextMenu = menu;
        return button;
    }

    void Hide(Item item)
    {
        Hidden.Add(item.Key);
        Refresh();
    }

    /// <summary>A pill that's ringed in the accent colour while on.</summary>
    Button Chip(string text, string glyph, bool on, Action click, string? tip = null)
    {
        var button = UI.Pill(text, click, glyph);
        button.Margin = new Thickness(0, 0, 6, 6);
        button.ToolTip = tip;
        if (button.Content is Border bg)
        {
            bg.BorderThickness = new Thickness(1.5);
            bg.BorderBrush = on ? Context.Accent() : Brushes.Transparent;
            bg.Padding = new Thickness(11.5, 4.5, 11.5, 4.5);
        }
        return button;
    }
}
