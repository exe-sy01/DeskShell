using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DeskShell.Services;
using ManagedShell.AppBar;

namespace DeskShell.Notch;

/// <summary>
/// The notch: a small black clock against the top edge of the screen, its edges breathing in a
/// colour of your choice. Rest the mouse on it and it grows into the Home panel: cards for screen
/// time, system load, weather, a focus timer, the clipboard and notes, with a tab bar for more.
/// </summary>
public sealed class NotchWindow : Window
{
    // Sizes in device-independent pixels. The body's width doesn't include the two flares (Ear).
    const double SmallW = 186, SmallH = 34, SmallR = 13;
    const double BigW = 760, BigH = 382, BigR = 28;
    const double PopW = 400, PopH = 70, PopR = 24;
    const double Ear = 11;
    /// <summary>Room around the shape for the glow.</summary>
    const double Room = 16;

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(NotchWindow),
        new PropertyMetadata(0.0, (d, _) => ((NotchWindow)d).Layout()));

    /// <summary>0 = the small notch, 1 = the open panel.</summary>
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    public static readonly DependencyProperty PopProgressProperty = DependencyProperty.Register(nameof(PopProgress), typeof(double), typeof(NotchWindow),
        new PropertyMetadata(0.0, (d, _) => ((NotchWindow)d).Layout()));

    /// <summary>0 = the small notch, 1 = grown wide for a notification.</summary>
    public double PopProgress { get => (double)GetValue(PopProgressProperty); set => SetValue(PopProgressProperty, value); }

    // ---------- notification pop-ups ----------

    readonly Grid _pop = new() { IsHitTestVisible = false };
    readonly Image _popIcon = new() { Width = 36, Height = 36, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _popBell = UI.Glyph("", 18, UI.Dim);
    readonly TextBlock _popTitle = UI.Label("", 13.5, bold: true);
    readonly TextBlock _popBody = new() { FontSize = 12, Foreground = UI.Dim, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap, MaxHeight = 32 };
    DispatcherTimer? _popTimer;
    Action? _popClick;
    bool _inPop;
    /// <summary>How many notches are on screen.</summary>
    static int _open;

    void BuildPop()
    {
        _pop.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        _pop.ColumnDefinitions.Add(new ColumnDefinition());
        _pop.Margin = new Thickness(0);
        var icon = new Grid { Margin = new Thickness(16, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Children = { _popBell, _popIcon } };
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 18, 2) };
        _popBody.FontFamily = UI.Font;
        words.Children.Add(_popTitle);
        words.Children.Add(_popBody);
        Grid.SetColumn(words, 1);
        _pop.Children.Add(icon);
        _pop.Children.Add(words);
        _pop.Opacity = 0;
        _canvas.Children.Add(_pop);

        NotificationWatcher.Arrived += OnToast;
        DiscordLink.MessageArrived += OnDiscordMessage;
        _open++;
        Closed += (_, _) =>
        {
            _open--;
            NotificationWatcher.Arrived -= OnToast;
            DiscordLink.MessageArrived -= OnDiscordMessage;
            _popTimer?.Stop();
        };
    }

    void OnToast(ToastItem item) => Dispatcher.BeginInvoke(() =>
    {
        var app = _context.Apps.Find(item.AppId);
        ShowPop(app?.Icon, false, app != null ? $"{app.Name} · {item.Title}" : item.Title, item.Body, ManagedShell.Common.Helpers.ShellHelper.ShowNotificationCenter);
    });

    /// <summary>Discord DMs and mentions (from the AIChat plugin); ones Discord marks quiet only go to the list.</summary>
    void OnDiscordMessage(DiscordMessage message) => Dispatcher.BeginInvoke(() =>
    {
        if (!App.Settings.Island.DiscordMessages || message.Quiet) return;
        ShowPop(DiscordLink.Avatar(message.Avatar), true, message.Title, message.Text, () => DiscordLink.Open(message));
    });

    /// <summary>Grows the notch wide to show a notification for a few seconds. Resting the mouse on it keeps it; clicking opens it.</summary>
    void ShowPop(ImageSource? icon, bool round, string title, string body, Action click)
    {
        // Not over a game or with Do Not Disturb on, and not while Home is open.
        if (!_style.Popups || !IsVisible || _expanded || GameMode.Active || DoNotDisturb.IsOn) return;
        // With a notch on every screen, only the one where you are pops up.
        if (_open > 1 && WindowManagement.ZoneManager.ScreenAtCursor().HMonitor != Screen.HMonitor) return;
        _popIcon.Source = icon;
        _popIcon.Clip = round ? new EllipseGeometry(new Point(18, 18), 18, 18) : null;
        _popIcon.Visibility = icon != null ? Visibility.Visible : Visibility.Collapsed;
        _popBell.Visibility = icon == null ? Visibility.Visible : Visibility.Collapsed;
        _popTitle.Text = title.ReplaceLineEndings(" ");
        _popBody.Text = body.ReplaceLineEndings(" ");
        _popBody.Visibility = body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _popClick = click;

        if (!_inPop)
        {
            _inPop = true;
            StopBreathing();
            SetSize(false);
            BeginAnimation(PopProgressProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(360))
            {
                EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut }
            });
        }
        _popTimer ??= new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _popTimer.Tick -= PopTimeUp;
        _popTimer.Tick += PopTimeUp;
        _popTimer.Stop();
        _popTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_style.PopupSeconds, 2, 30));
        if (!IsMouseOver) _popTimer.Start();
    }

    void PopTimeUp(object? sender, EventArgs e) => EndPop();

    void EndPop(bool animate = true)
    {
        _popTimer?.Stop();
        if (!_inPop) return;
        _inPop = false;
        if (!animate)
        {
            BeginAnimation(PopProgressProperty, null);
            PopProgress = 0;
            return;
        }
        var shrink = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
        shrink.Completed += (_, _) =>
        {
            if (_inPop || _expanded) return;
            SetSize(false);
            StartBreathing();
        };
        BeginAnimation(PopProgressProperty, shrink);
    }

    readonly NotchStyle _style = App.Settings.Notch;
    readonly Canvas _canvas = new() { ClipToBounds = false };
    readonly Path _body = new() { StrokeThickness = 1 };
    readonly NotchGlow _glowWin;
    readonly DispatcherTimer _breathTimer;
    readonly System.Diagnostics.Stopwatch _breathClock = System.Diagnostics.Stopwatch.StartNew();
    readonly Grid _small = new() { Height = SmallH };
    readonly TextBlock _clock = UI.Label("", 13.5, bold: true);
    readonly TextBlock _side = UI.Label("", 12.5, bold: true);
    readonly Grid _panel = new() { Width = BigW, Height = BigH };
    readonly Border _pageHost = new();
    readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    readonly TextBlock _headGlyph = UI.Glyph("", 13, Brushes.Black);
    readonly TextBlock _headTitle = UI.Label("", 15, bold: true);
    readonly TextBlock _headDate = UI.Label("", 13, brush: UI.Dim);
    readonly Popup _colours = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom };
    readonly DispatcherTimer _hoverTimer, _leaveTimer, _tickTimer, _clockTimer;
    readonly Dictionary<string, NotchPage> _pages = new();
    readonly NotchContext _context;
    readonly MediaService _media;
    Brush _accent;
    NotchPage? _page;
    double _winW, _winH;
    bool _expanded, _allowClose, _menuOpen, _musicPlaying;
    IntPtr _previousForeground;

    public AppBarScreen Screen { get; }

    public NotchWindow(ManagedShell.ShellManager shell, AppBarScreen screen, MediaService media, ClipboardHistory clipboard, AppIndex apps)
    {
        Screen = screen;
        _media = media;
        Title = "DeskShell Notch";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Left = Top = -20000;
        FontFamily = UI.Font;
        Foreground = UI.Text;
        ThemeManager.AlwaysDark(Resources);
        _accent = UI.Frozen(UI.Parse(_style.PanelColor, Color.FromRgb(0xFF, 0x6A, 0x1F)));

        _context = new NotchContext
        {
            Shell = shell,
            Media = media,
            Clipboard = clipboard,
            Apps = apps,
            Accent = () => _accent,
            ShowTab = ShowTab,
            Collapse = () => Collapse(),
            Activate = TakeFocus
        };

        _glowWin = new NotchGlow(UI.Parse(_style.GlowColor, Color.FromRgb(0xE0, 0x40, 0xFB)));
        // Breathing: one slow in-and-out every 4.8 seconds, stepped 15 times a second.
        _breathTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(66), DispatcherPriority.Background, (_, _) =>
        {
            var phase = _breathClock.Elapsed.TotalSeconds / 4.8 * 2 * Math.PI;
            _glowWin.SetStrength(0.25 + 0.75 * (0.5 - 0.5 * Math.Cos(phase)));
        }, Dispatcher);
        _breathTimer.Stop();
        BuildShape();
        BuildSmall();
        BuildPop();
        BuildPanel();
        Content = _canvas;

        _hoverTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(Math.Clamp(_style.HoverDelay, 0, 2000)), DispatcherPriority.Normal, (_, _) => { _hoverTimer!.Stop(); Expand(); }, Dispatcher);
        _hoverTimer.Stop();
        _leaveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(380), DispatcherPriority.Normal, (_, _) => { _leaveTimer!.Stop(); if (!KeepOpen) Collapse(); }, Dispatcher);
        _leaveTimer.Stop();
        _tickTimer = new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, (_, _) => _page?.Tick(), Dispatcher);
        _tickTimer.Stop();
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
        _clockTimer.Tick += (_, _) => UpdateClock();

        MouseEnter += (_, _) =>
        {
            _leaveTimer.Stop();
            // Over a notification: keep it up (click opens it) instead of opening Home.
            if (_inPop) _popTimer?.Stop();
            else if (!_expanded) _hoverTimer.Start();
        };
        MouseLeave += (_, _) =>
        {
            _hoverTimer.Stop();
            if (_inPop && _popTimer != null)
            {
                _popTimer.Interval = TimeSpan.FromSeconds(2);
                _popTimer.Start();
            }
            if (_expanded && !KeepOpen) _leaveTimer.Start();
        };
        Deactivated += (_, _) =>
        {
            if (_expanded && !IsMouseOver && !KeepOpen) Collapse(giveFocusBack: false);
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _expanded)
            {
                Collapse();
                e.Handled = true;
            }
        };
        _colours.Closed += (_, _) => { if (_expanded && !IsMouseOver) _leaveTimer.Start(); };

        _media.Changed += OnMediaChanged;
        FocusTimer.Changed += UpdateSide;
        DiscordLink.CallChanged += OnCallChanged;
        if (App.Fullscreen != null) App.Fullscreen.Changed += UpdateVisibility;

        SourceInitialized += (_, _) => Native.MakeNonActivating(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) =>
        {
            SetSize(false);
            UpdateClock();
            UpdateSide();
            OnMediaChanged();
            UpdateVisibility();
        };
        Closed += (_, _) =>
        {
            _media.Changed -= OnMediaChanged;
            FocusTimer.Changed -= UpdateSide;
            DiscordLink.CallChanged -= OnCallChanged;
            _speakerHold?.Stop();
            if (App.Fullscreen != null) App.Fullscreen.Changed -= UpdateVisibility;
            _clockTimer.Stop();
            _tickTimer.Stop();
            _breathTimer.Stop();
            _glowWin.Close();
            ThemeManager.Release(Resources);
        };
    }

    /// <summary>A menu or the colour picker is open, or you're typing in the panel: don't fold away.</summary>
    bool KeepOpen => _menuOpen || _colours.IsOpen || (IsActive && Keyboard.FocusedElement is TextBox);

    // ---------- the shape and its glow ----------

    void BuildShape()
    {
        _body.Fill = Brushes.Black;
        _body.Stroke = UI.Frozen(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        _canvas.Children.Add(_body);
    }

    /// <summary>Shows the glow over the small notch, breathing if chosen.</summary>
    void StartBreathing()
    {
        if (!_style.Glow || _expanded || _inPop || !IsVisible)
        {
            StopBreathing();
            return;
        }
        if (!_glowWin.IsVisible) _glowWin.Show();
        PlaceGlow();
        if (_style.Breathing) _breathTimer.Start();
        else
        {
            _breathTimer.Stop();
            _glowWin.SetStrength(1);
        }
    }

    void StopBreathing()
    {
        _breathTimer.Stop();
        if (_glowWin.IsVisible) _glowWin.Hide();
    }

    // ---------- the small notch: clock, and a timer or music note on the right ----------

    void BuildSmall()
    {
        _small.ColumnDefinitions.Add(new ColumnDefinition());
        _small.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _clock.Margin = new Thickness(15, 0, 0, 1);
        _side.Margin = new Thickness(0, 0, 14, 1);
        _speakers.Margin = new Thickness(0, 0, 10, 0);
        var sideHost = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { _speakers, _side } };
        Grid.SetColumn(sideHost, 1);
        _small.Children.Add(_clock);
        _small.Children.Add(sideHost);
        _small.IsHitTestVisible = false;
        _canvas.Children.Add(_small);

        var menu = new ContextMenu();
        menu.Items.Add(Item("Open Home", () => Expand()));
        menu.Items.Add(Item("Open the shell", () => App.Instance.ToggleShell()));
        menu.Items.Add(Item("All apps", () => App.Instance.ToggleAppDrawer()));
        var tools = new MenuItem { Header = "Tools" };
        ToolsMenu.Fill(tools);
        menu.Items.Add(tools);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("DeskShell settings", () => App.Instance.ShowSettings()));
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => _menuOpen = false;
        _body.ContextMenu = menu;
        _body.MouseLeftButtonUp += (_, _) =>
        {
            if (_expanded) return;
            _hoverTimer.Stop();
            if (_inPop)
            {
                var open = _popClick;
                EndPop();
                open?.Invoke();
                return;
            }
            Expand();
        };
    }

    static MenuItem Item(string header, Action click)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => click();
        return item;
    }

    void UpdateClock()
    {
        var now = DateTime.Now;
        _clock.Text = now.ToString(_style.Seconds ? "HH:mm:ss" : "HH:mm");
        _headDate.Text = now.ToString("dddd, d MMM");
        // Wake again exactly when the shown time changes.
        _clockTimer.Interval = _style.Seconds ? TimeSpan.FromMilliseconds(1000 - now.Millisecond + 5)
            : TimeSpan.FromMilliseconds((60 - now.Second) * 1000 - now.Millisecond + 20);
        _clockTimer.Start();
    }

    // In a call: who's talking (and who talked a moment ago, so faces don't flicker between words)
    readonly StackPanel _speakers = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly Dictionary<string, long> _lastSpoke = new();
    DispatcherTimer? _speakerHold;
    const long SpeakerHoldMs = 1200;

    static bool InCall => App.Settings.Island.ShowCalls && DiscordLink.Call != null;

    void OnCallChanged()
    {
        // The Voice call tab comes and goes with the call.
        var hasTab = _tabs.Children.OfType<Button>().Any(b => b.Content is Border { Tag: "call" });
        if (hasTab != InCall)
        {
            BuildTabs();
            if (!InCall && _page?.Id == "call") ShowTab("home");
            else MarkTab();
        }
        if (IsVisible) UpdateSide();
    }

    void UpdateSide()
    {
        _speakers.Children.Clear();
        if (InCall)
        {
            var call = DiscordLink.Call!;
            var now = Environment.TickCount64;
            foreach (var p in call.Peers.Where(p => p.Speaking)) _lastSpoke[p.Id] = now;
            var talking = call.Peers.Where(p => p.Speaking || now - _lastSpoke.GetValueOrDefault(p.Id, long.MinValue / 2) < SpeakerHoldMs).ToList();
            foreach (var peer in talking.Take(4))
            {
                var face = Avatars.Make(peer, 22, badge: false);
                face.Margin = new Thickness(2, 0, 0, 0);
                _speakers.Children.Add(face);
            }
            _side.FontFamily = talking.Count > 4 ? UI.Font : UI.Icons;
            _side.FontSize = 12;
            _side.Text = talking.Count > 4 ? $"+{talking.Count - 4}" : talking.Count == 0 ? "" : "";
            _side.Foreground = Avatars.Talking;
            _side.Margin = new Thickness(0, 0, talking.Count > 0 && talking.Count <= 4 ? 4 : 14, 1);
            // Look again when someone's moment after talking runs out.
            if (talking.Any(p => !p.Speaking))
            {
                _speakerHold ??= new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => { _speakerHold!.Stop(); UpdateSide(); }, Dispatcher);
                _speakerHold.Start();
            }
            return;
        }
        _side.Margin = new Thickness(0, 0, 14, 1);
        if (FocusTimer.Running || FocusTimer.Current != FocusTimer.Phase.Ready)
        {
            _side.FontFamily = UI.Font;
            _side.FontSize = 12.5;
            _side.Text = FocusTimer.Text;
            _side.Foreground = FocusTimer.Running ? _accent : UI.Dim;
        }
        else if (_musicPlaying)
        {
            _side.FontFamily = UI.Icons;
            _side.FontSize = 12;
            _side.Text = "";
            _side.Foreground = _accent;
        }
        else _side.Text = "";
    }

    void OnMediaChanged() => Dispatcher.BeginInvoke(async () =>
    {
        var now = await _media.GetAsync();
        _musicPlaying = now?.IsPlaying == true;
        UpdateSide();
    });

    // ---------- the open panel ----------

    void BuildPanel()
    {
        _panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        _panel.RowDefinitions.Add(new RowDefinition());
        _panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _panel.Margin = new Thickness(0);

        // Header: the tab's icon and name, today's date; colours, settings and close on the right.
        var badge = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Background = Brushes.White, Child = _headGlyph };
        _headTitle.Margin = new Thickness(11, 0, 0, 0);
        var divider = new Border { Width = 1, Height = 18, Background = UI.Faint, Margin = new Thickness(16, 0, 16, 0) };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Children = { badge, _headTitle, divider, _headDate } };
        var colours = HeaderButton("", "Colours", () => _colours.IsOpen = !_colours.IsOpen);
        _colours.PlacementTarget = colours;
        _colours.Child = BuildColourPicker();
        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Children =
            {
                colours,
                HeaderButton("", "DeskShell settings", () => { Collapse(); App.Instance.ShowSettings(); }),
                HeaderButton("", "Close", () => Collapse())
            }
        };
        var header = new Grid { Margin = new Thickness(20, 14, 16, 0), Children = { left, right } };
        _panel.Children.Add(header);

        _pageHost.Margin = new Thickness(14, 12, 14, 10);
        Grid.SetRow(_pageHost, 1);
        _panel.Children.Add(_pageHost);

        var bar = new Border
        {
            Background = UI.Frozen(Color.FromRgb(0x10, 0x10, 0x10)),
            BorderBrush = UI.CardEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(26),
            Padding = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 14),
            Child = _tabs
        };
        Grid.SetRow(bar, 2);
        _panel.Children.Add(bar);
        BuildTabs();

        _panel.Visibility = Visibility.Collapsed;
        _canvas.Children.Add(_panel);
    }

    static Button HeaderButton(string glyph, string tip, Action click)
    {
        var bg = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Background = Brushes.Transparent, Child = UI.Glyph(glyph, 13, UI.Dim) };
        var button = new Button { Content = bg, Template = UI.Bare(), Cursor = Cursors.Hand, Focusable = false, ToolTip = tip, Margin = new Thickness(4, 0, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(button, tip);
        button.MouseEnter += (_, _) => bg.Background = UI.Raised;
        button.MouseLeave += (_, _) => bg.Background = Brushes.Transparent;
        button.Click += (_, _) => click();
        return button;
    }

    void BuildTabs()
    {
        _tabs.Children.Clear();
        var ids = _style.Tabs.Where(id => id != "call" && NotchPage.All.Any(t => t.Id == id)).ToList();
        if (!ids.Contains("home")) ids.Insert(0, "home");
        if (InCall) ids.Insert(1, "call"); // only while you're in a call
        foreach (var id in ids)
        {
            var (_, title, glyph) = NotchPage.All.First(t => t.Id == id);
            var icon = UI.Glyph(glyph, 14, UI.Frozen(Color.FromRgb(0xC8, 0xC8, 0xC8)));
            var bg = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(19), Background = Brushes.Transparent, Child = icon, Tag = id };
            var button = new Button { Content = bg, Template = UI.Bare(), Cursor = Cursors.Hand, Focusable = false, ToolTip = title, Margin = new Thickness(1, 0, 1, 0) };
            System.Windows.Automation.AutomationProperties.SetName(button, title);
            button.MouseEnter += (_, _) => { if (_page?.Id != id) bg.Background = UI.Raised; };
            button.MouseLeave += (_, _) => { if (_page?.Id != id) bg.Background = Brushes.Transparent; };
            button.Click += (_, _) => ShowTab(id);
            _tabs.Children.Add(button);
        }
        _tabs.Children.Add(new Border { Width = 1, Height = 22, Background = UI.Faint, Margin = new Thickness(6, 0, 6, 0) });

        // + : choose which tabs the bar shows
        var add = HeaderButton("", "Choose tabs", () => { });
        add.Margin = new Thickness(0, 0, 2, 0);
        ((Border)add.Content).Width = ((Border)add.Content).Height = 38;
        ((Border)add.Content).CornerRadius = new CornerRadius(19);
        var menu = new ContextMenu();
        foreach (var (id, title, _) in NotchPage.All.Where(t => t.Id is not "home" and not "call"))
        {
            var item = new MenuItem { Header = title, IsCheckable = true, IsChecked = ids.Contains(id), StaysOpenOnClick = true };
            item.Click += (_, _) =>
            {
                var tabs = NotchPage.All.Select(t => t.Id).Where(t => t == "home" || (t == id ? item.IsChecked : _style.Tabs.Contains(t))).ToList();
                _style.Tabs = tabs;
                App.Settings.Save();
                BuildTabs();
                MarkTab();
            };
            menu.Items.Add(item);
        }
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            if (_expanded && !IsMouseOver) _leaveTimer.Start();
        };
        add.ContextMenu = menu;
        add.Click += (_, _) =>
        {
            menu.PlacementTarget = add;
            menu.Placement = PlacementMode.Top;
            menu.IsOpen = true;
        };
        _tabs.Children.Add(add);
    }

    void MarkTab()
    {
        foreach (var button in _tabs.Children.OfType<Button>())
        {
            if (button.Content is not Border { Tag: string id } bg) continue;
            var on = _page?.Id == id;
            bg.Background = on ? Brushes.White : Brushes.Transparent;
            ((TextBlock)bg.Child).Foreground = on ? Brushes.Black : UI.Frozen(Color.FromRgb(0xC8, 0xC8, 0xC8));
        }
    }

    void ShowTab(string id)
    {
        if (!_pages.TryGetValue(id, out var page)) _pages[id] = page = NotchPage.Create(id, _context);
        _page = page;
        _pageHost.Child = page.View;
        _headGlyph.Text = page.Glyph;
        _headTitle.Text = page.Title;
        MarkTab();
        page.Refresh();
    }

    UIElement BuildColourPicker()
    {
        string[] swatches = ["#E040FB", "#FF6A1F", "#FF3B5C", "#FFC233", "#3DDC84", "#22D3EE", "#3B82F6", "#8B5CF6", "#FFFFFF"];
        var stack = new StackPanel();

        WrapPanel Row(string title, Func<string> current, Action<string> pick)
        {
            stack.Children.Add(new TextBlock { Text = title, Foreground = UI.Dim, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, stack.Children.Count == 0 ? 0 : 10, 0, 6) });
            var row = new WrapPanel { Width = 9 * 28 };
            foreach (var hex in swatches)
            {
                var colour = UI.Parse(hex, Colors.White);
                var dot = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = UI.Frozen(colour), BorderThickness = new Thickness(2) };
                var button = new Button { Content = dot, Template = UI.Bare(), Cursor = Cursors.Hand, Focusable = false, Margin = new Thickness(0, 0, 8, 0), Tag = hex };
                button.Click += (_, _) =>
                {
                    pick(hex);
                    App.Settings.Save();
                    Mark(row, current());
                };
                row.Children.Add(button);
            }
            Mark(row, current());
            stack.Children.Add(row);
            return row;
        }

        static void Mark(WrapPanel row, string current)
        {
            foreach (var b in row.Children.OfType<Button>())
                ((Border)b.Content).BorderBrush = string.Equals((string)b.Tag, current, StringComparison.OrdinalIgnoreCase) ? Brushes.White : Brushes.Transparent;
        }

        Row("EDGE GLOW", () => _style.GlowColor, hex => { _style.GlowColor = hex; ApplyGlowColour(); });
        Row("PANEL", () => _style.PanelColor, hex =>
        {
            _style.PanelColor = hex;
            _accent = UI.Frozen(UI.Parse(hex, Colors.OrangeRed));
            UpdateSide();
            _page?.Refresh();
        });

        var breathing = UI.Pill(BreathingText(), () => { });
        breathing.Margin = new Thickness(0, 12, 0, 0);
        breathing.HorizontalAlignment = HorizontalAlignment.Left;
        breathing.Click += (_, _) =>
        {
            _style.Breathing = !_style.Breathing;
            App.Settings.Save();
            if (!_expanded) StartBreathing(); // shows the change once the panel closes otherwise
            ((TextBlock)((StackPanel)((Border)breathing.Content).Child).Children[^1]).Text = BreathingText();
        };
        var popups = UI.Pill(PopupsText(), () => { });
        popups.Margin = new Thickness(8, 12, 0, 0);
        popups.Click += (_, _) =>
        {
            _style.Popups = !_style.Popups;
            App.Settings.Save();
            ((TextBlock)((StackPanel)((Border)popups.Content).Child).Children[^1]).Text = PopupsText();
        };
        stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { breathing, popups } });

        return new Border
        {
            Background = UI.Frozen(Color.FromRgb(0x18, 0x18, 0x18)),
            BorderBrush = UI.CardEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 12, 6, 12),
            Margin = new Thickness(0, 6, 0, 0),
            Child = stack
        };
    }

    string BreathingText() => _style.Breathing ? "Breathing: on" : "Breathing: off";
    string PopupsText() => _style.Popups ? "Pop-ups: on" : "Pop-ups: off";

    void ApplyGlowColour() => _glowWin.SetColour(UI.Parse(_style.GlowColor, Color.FromRgb(0xE0, 0x40, 0xFB)));

    // ---------- opening and closing ----------

    /// <summary>From the hotkey: open Home with the keyboard (Esc or clicking elsewhere closes it), or close it.</summary>
    public void Toggle()
    {
        if (_expanded)
        {
            Collapse();
            return;
        }
        Expand();
        TakeFocus();
    }

    void Expand()
    {
        if (_expanded || !IsVisible) return;
        _expanded = true;
        _leaveTimer.Stop();
        if (_inPop)
        {
            // Grow straight from the notification into Home.
            _popTimer?.Stop();
            _inPop = false;
            BeginAnimation(PopProgressProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(380)));
        }
        StopBreathing();
        SetSize(true);
        _panel.Visibility = Visibility.Visible;
        ShowTab(_page?.Id ?? "home");
        _tickTimer.Start();
        var open = new DoubleAnimation(1, TimeSpan.FromMilliseconds(380))
        {
            EasingFunction = new BackEase { Amplitude = 0.18, EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(ProgressProperty, open);
    }

    void Collapse(bool giveFocusBack = true)
    {
        if (!_expanded) return;
        _expanded = false;
        _hoverTimer.Stop();
        _leaveTimer.Stop();
        _tickTimer.Stop();
        _colours.IsOpen = false;
        if (giveFocusBack && IsActive && _previousForeground != IntPtr.Zero) Native.SetForegroundWindow(_previousForeground);
        _previousForeground = IntPtr.Zero;
        Keyboard.ClearFocus();
        var close = new DoubleAnimation(0, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        close.Completed += (_, _) =>
        {
            if (_expanded) return;
            _panel.Visibility = Visibility.Collapsed;
            SetSize(false);
            StartBreathing();
            App.Instance.TrimMemorySoon();
        };
        BeginAnimation(ProgressProperty, close);
    }

    /// <summary>Typing in the panel: take the keyboard (and give it back to your app when the panel closes).</summary>
    void TakeFocus()
    {
        if (IsActive) return;
        _previousForeground = GetForegroundWindow();
        WindowManagement.WmNative.Activate(new WindowInteropHelper(this).Handle);
    }

    /// <summary>Sizes the window for the open panel (big), a notification, or the small notch.</summary>
    void SetSize(bool big)
    {
        _winW = (big ? BigW : _inPop ? PopW : SmallW) + 2 * Ear + 2 * Room;
        _winH = (big ? BigH : _inPop ? PopH : SmallH) + Room;
        Width = _winW;
        Height = _winH;
        _canvas.Width = _winW;
        _canvas.Height = _winH;
        Place();
        Layout();
    }

    /// <summary>Top centre of its monitor, in physical pixels (the window is per-monitor DPI aware).</summary>
    public void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var scale = WindowManagement.WmNative.ScaleFor(Screen.HMonitor);
        var b = Screen.Bounds;
        var w = (int)Math.Round(_winW * scale);
        var h = (int)Math.Round(_winH * scale);
        for (var i = 0; i < 2; i++)
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, (int)(b.Left + (b.Width - w) / 2), (int)b.Top, w, h, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        if (_glowWin.IsVisible) PlaceGlow();
    }

    /// <summary>The glow window sits exactly over the small notch, above it.</summary>
    void PlaceGlow()
    {
        var scale = WindowManagement.WmNative.ScaleFor(Screen.HMonitor);
        var b = Screen.Bounds;
        var w = (int)Math.Round(_winW * scale);
        var h = (int)Math.Round(_winH * scale);
        for (var i = 0; i < 2; i++)
            _glowWin.PlaceOver((int)(b.Left + (b.Width - w) / 2), (int)b.Top, w, h, _winW, _winH);
    }

    /// <summary>Draws the shape for the current <see cref="Progress"/>, between the small notch and the open panel.</summary>
    void Layout()
    {
        if (_winW <= 0) return;
        var p = Progress;
        var m = PopProgress;
        // Small → (a notification's size) → the open panel
        var w0 = SmallW + (PopW - SmallW) * m;
        var h0 = SmallH + (PopH - SmallH) * m;
        var r0 = SmallR + (PopR - SmallR) * Math.Clamp(m, 0, 1);
        var w = w0 + (BigW - w0) * p;
        var h = h0 + (BigH - h0) * p;
        var r = r0 + (BigR - r0) * Math.Clamp(p, 0, 1);
        var left = (_winW - (w + 2 * Ear)) / 2;

        _body.Data = NotchShape.Fill(left, w, h, Ear, r);
        if (p <= 0.0001 && m <= 0.0001 && !_expanded) _glowWin.SetEdge(NotchShape.Edge(left, w, h, Ear, r));

        Canvas.SetLeft(_small, left + Ear);
        _small.Width = w;
        _small.Opacity = Math.Clamp(1 - p * 4, 0, 1) * Math.Clamp(1 - m * 3, 0, 1);

        Canvas.SetLeft(_pop, left + Ear);
        _pop.Width = w;
        _pop.Height = h;
        _pop.Opacity = Math.Clamp((m - 0.45) / 0.45, 0, 1) * Math.Clamp(1 - p * 4, 0, 1);
        _pop.Clip = new RectangleGeometry(new Rect(0, 0, w, h));

        var panelLeft = (_winW - BigW) / 2;
        Canvas.SetLeft(_panel, panelLeft);
        Canvas.SetTop(_panel, 0);
        _panel.Opacity = Math.Clamp((p - 0.45) / 0.45, 0, 1);
        // Keep the panel inside the growing shape.
        _panel.Clip = NotchShape.Fill(left - panelLeft, w, h, Ear, r);
    }

    void UpdateVisibility()
    {
        var covered = App.Settings.Island.HideFullscreen && App.Fullscreen?.IsCovered(Screen.HMonitor) == true;
        Visibility = covered ? Visibility.Hidden : Visibility.Visible;
        if (covered)
        {
            EndPop(animate: false);
            Collapse(giveFocusBack: false);
            StopBreathing();
        }
        else if (!_expanded) StartBreathing();
    }

    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose) e.Cancel = true; // Alt+F4 can't close it
        base.OnClosing(e);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
}
