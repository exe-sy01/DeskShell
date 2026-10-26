using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DeskShell.Services;
using ManagedShell;
using ManagedShell.AppBar;
using ManagedShell.Common.Helpers;
using ManagedShell.WindowsTasks;

namespace DeskShell;

/// <summary>
/// The island: a black pill at the top of the screen, a fixed size, with ‹ › to flip between
/// pages (notifications, music, open windows, pinned programs, clock). It opens the shell
/// (double-click by default) and reacts to new notifications and songs if asked to. Its look
/// and behaviour come from <see cref="IslandStyle"/> (designed in Island Workshop).
/// </summary>
public partial class IslandWindow : Window
{
    /// <summary>Extra height the pill grows to for a notification moment.</summary>
    const double MomentGrow = 26;
    const double PeekWidth = 140, PeekHeight = 7;

    static readonly Dictionary<string, string> PageNames = new()
    {
        ["notifications"] = "Notifications", ["music"] = "Music", ["windows"] = "Open windows", ["programs"] = "Programs", ["clock"] = "Clock",
        ["call"] = "Voice call"
    };

    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(IslandWindow), new PropertyMetadata(18.0));
    public static readonly DependencyProperty ShowActiveTitleProperty =
        DependencyProperty.Register(nameof(ShowActiveTitle), typeof(bool), typeof(IslandWindow), new PropertyMetadata(false));

    /// <summary>Icon size on the windows and programs pages (bound by their templates).</summary>
    public double IconSize { get => (double)GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }
    /// <summary>Name the window in use next to its icon (bound by the window template).</summary>
    public bool ShowActiveTitle { get => (bool)GetValue(ShowActiveTitleProperty); set => SetValue(ShowActiveTitleProperty, value); }

    readonly ShellManager _shell;
    readonly MediaService _media;
    readonly AppIndex _apps;
    readonly ICollectionView? _windows;
    readonly DispatcherTimer _pageTimer;
    readonly DispatcherTimer _peekTimer;
    readonly DispatcherTimer _hoverOpenTimer;
    readonly DispatcherTimer _momentTimer;
    IslandStyle _style = App.Settings.Island;
    List<string> _pages = new();
    int _page;
    bool _allowClose, _menuOpen, _peeking, _inMoment, _hovered, _inCall;
    string? _pageBeforeCall;
    string? _pageBeforeMoment;
    string? _lastTrack;
    bool _lastPlaying;

    // Pages, built once and updated in place
    readonly Grid _musicPage = new();
    readonly Border _art = new() { Width = 26, Height = 26, CornerRadius = new CornerRadius(6) };
    readonly TextBlock _musicTitle = Line(12.5, bold: true);
    readonly TextBlock _musicArtist = Line(11, dim: true);
    readonly TextBlock _playGlyph = Glyph("", 12);
    readonly StackPanel _musicControls = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _bars = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Height = 14 };
    readonly Grid _notificationsPage = new();
    readonly Image _notificationIcon = new() { Width = 18, Height = 18 };
    readonly TextBlock _notificationTitle = Line(12.5, bold: true);
    readonly TextBlock _notificationBody = Line(11, dim: true);
    readonly TextBlock _notificationCount = Line(11, bold: true);
    readonly Border _countChip = new();
    readonly Grid _windowsPage = new();
    readonly ScrollViewer _windowsScroll = IconStrip();
    readonly TextBlock _noWindows = Empty("No open windows");
    readonly Grid _programsPage = new();
    readonly ScrollViewer _programsScroll = IconStrip();
    readonly TextBlock _noPrograms = Empty("Right-click a window or app and choose Pin");
    readonly StackPanel _clockPage = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _clockTime = Line(17, bold: true);
    readonly TextBlock _clockDate = Line(12, dim: true);
    readonly Grid _callPage = new();
    readonly TextBlock _callChannel = Line(12.5, bold: true);
    readonly TextBlock _callServer = Line(11, dim: true);
    readonly StackPanel _callPeople = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _callMore = Line(11, dim: true);
    readonly Dictionary<string, PeerView> _peerViews = new();
    static readonly Dictionary<string, ImageSource> AvatarCache = new();

    public AppBarScreen Screen { get; private set; }

    public IslandWindow(ShellManager shell, AppBarScreen screen, MediaService media, AppIndex apps)
    {
        InitializeComponent();
        ThemeManager.AlwaysDark(Resources);
        _shell = shell;
        Screen = screen;
        _media = media;
        _apps = apps;

        _windows = shell.Tasks.CreateGroupedWindowsCollection();
        if (_windows != null)
        {
            _windows.Filter = IncludeWindow;
            if (_windows is ICollectionViewLiveShaping live)
            {
                live.IsLiveFiltering = true;
                live.LiveFilteringProperties.Add(nameof(ApplicationWindow.ShowInTaskbar));
                live.LiveFilteringProperties.Add(nameof(ApplicationWindow.HMonitor));
            }
        }
        // While the notifications or clock page shows, refresh it now and then.
        _pageTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => RefreshPage(), Dispatcher);
        _pageTimer.Stop();
        _peekTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Normal, (_, _) => { _peekTimer!.Stop(); SetPeek(true); }, Dispatcher);
        _peekTimer.Stop();
        _hoverOpenTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(650), DispatcherPriority.Normal, (_, _) => { _hoverOpenTimer!.Stop(); App.Instance.OpenShellFrom(this); }, Dispatcher);
        _hoverOpenTimer.Stop();
        _momentTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _momentTimer.Tick += (_, _) => EndMoment();
        BuildPages();
        ApplyStyle();

        _media.Changed += OnMediaChanged;
        NotificationWatcher.Arrived += OnNotification;
        DiscordVoice.Changed += OnCallChanged;
        DockPins.Items.CollectionChanged += (_, _) => UpdateEmptyStates();
        if (_windows is System.Collections.Specialized.INotifyCollectionChanged changes)
            changes.CollectionChanged += (_, _) => UpdateEmptyStates();
        if (App.Fullscreen != null) App.Fullscreen.Changed += UpdateVisibility;
        _ = RefreshMusicAsync(announce: false);

        SourceInitialized += (_, _) => Native.MakeNonActivating(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => { Place(); UpdateVisibility(); };
        Closed += (_, _) =>
        {
            _media.Changed -= OnMediaChanged;
            NotificationWatcher.Arrived -= OnNotification;
            DiscordVoice.Changed -= OnCallChanged;
            _pageTimer.Stop();
            _momentTimer.Stop();
            if (App.Fullscreen != null) App.Fullscreen.Changed -= UpdateVisibility;
        };
    }

    bool IncludeWindow(object item) =>
        item is ApplicationWindow { ShowInTaskbar: true } window &&
        (_style.WindowsFromAllMonitors || window.HMonitor == Screen.HMonitor);

    // ---------- style ----------

    /// <summary>Sizes, colours and options from the settings.</summary>
    void ApplyStyle()
    {
        var s = _style;
        Width = s.Width + 48;
        Height = s.TopGap + s.Height + MomentGrow + 28;
        foreach (var b in new[] { Pill, Shadow })
        {
            b.Margin = new Thickness(0, s.TopGap, 0, 0);
            b.Width = s.Width;
            b.Height = s.Height;
            b.CornerRadius = new CornerRadius(s.CornerRadius);
        }

        Pill.Background = s.Surface switch
        {
            "glass" => new SolidColorBrush(Color.FromArgb(0xB0, 0x12, 0x11, 0x16)),
            "tint" => new SolidColorBrush(Mix(Accent(), Color.FromRgb(0x05, 0x04, 0x07), 0.16)),
            _ => (Brush)FindResource("IslandBrush")
        };
        if (s.Edge == "accent") Pill.BorderBrush = new SolidColorBrush(Color.FromArgb(0xB3, Accent().R, Accent().G, Accent().B));
        else if (s.Edge == "none") Pill.BorderBrush = Brushes.Transparent;
        else Pill.SetResourceReference(Border.BorderBrushProperty, "IslandEdgeBrush");

        Shadow.Visibility = s.Shadow == "none" ? Visibility.Collapsed : Visibility.Visible;
        ShadowEffect.BlurRadius = s.Shadow == "strong" ? 30 : 16;
        ShadowEffect.ShadowDepth = s.Shadow == "strong" ? 7 : 3;
        ShadowEffect.Opacity = s.Shadow == "strong" ? 0.75 : 0.5;
        // A see-through surface would show the black shadow block through it; keep only its blur.
        Shadow.Opacity = s.Surface == "glass" ? 0.0 : 1.0;

        PrevButton.Visibility = NextButton.Visibility = s.Arrows == "hidden" ? Visibility.Collapsed : Visibility.Visible;
        SetArrowOpacity(s.Arrows == "hover" ? 0 : 1);
        // With page dots, lift the contents so a second line of text clears them.
        var side = s.Arrows == "hidden" ? 12 : 5;
        Contents.Margin = new Thickness(side, 0, side, s.PageDots ? 6 : 0);

        IconSize = s.IconSize;
        ShowActiveTitle = s.ActiveWindowTitle;
        _art.Visibility = s.MusicArt ? Visibility.Visible : Visibility.Collapsed;
        _musicArtist.Visibility = s.MusicArtist && _musicArtist.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _musicControls.Visibility = s.MusicControls ? Visibility.Visible : Visibility.Collapsed;
        _notificationBody.Visibility = s.NotificationText && _notificationBody.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateBars();

        _inCall = s.ShowCalls && DiscordVoice.Current != null;
        UpdateCallPage();
        RebuildPages(s.StartOnLastPage ? App.Settings.IslandPage : null);

        _peeking = false;
        if (s.Peek) SetPeek(true, animate: false);
    }

    static Color Accent() => Application.Current.Resources["AccentColor"] is Color c ? c : Color.FromRgb(0xD2, 0x9E, 0xDB);

    static Color Mix(Color a, Color b, double amountOfA) => Color.FromRgb(
        (byte)(a.R * amountOfA + b.R * (1 - amountOfA)), (byte)(a.G * amountOfA + b.G * (1 - amountOfA)), (byte)(a.B * amountOfA + b.B * (1 - amountOfA)));

    void SetArrowOpacity(double to)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(150));
        PrevButton.BeginAnimation(OpacityProperty, anim);
        NextButton.BeginAnimation(OpacityProperty, anim);
    }

    // ---------- placement ----------

    /// <summary>Top centre of its monitor, in physical pixels (the window is per-monitor DPI aware).</summary>
    public void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var scale = WindowManagement.WmNative.ScaleFor(Screen.HMonitor);
        var b = Screen.Bounds;
        var w = (int)Math.Round(Width * scale);
        var h = (int)Math.Round(Height * scale);
        // Twice: the first move may land on a monitor with a different scale and trigger a DPI resize.
        for (var i = 0; i < 2; i++)
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, (int)(b.Left + (b.Width - w) / 2), (int)b.Top, w, h, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    /// <summary>Where the full pill is on screen, in physical pixels (for the shell's expand animation), even while peeking.</summary>
    public Rect PillScreenRect
    {
        get
        {
            var left = (ActualWidth - _style.Width) / 2;
            var topLeft = PointToScreen(new Point(left, _style.TopGap));
            var bottomRight = PointToScreen(new Point(left + _style.Width, _style.TopGap + _style.Height));
            return new Rect(topLeft, bottomRight);
        }
    }

    public double PillCornerRadius => _style.CornerRadius;

    public bool IsShowingPill => IsVisible && Pill.Visibility == Visibility.Visible;

    /// <summary>While the shell is open the island "is" the shell: the pill hides and the shell grows out of its place.</summary>
    public void SetExpanded(bool expanded)
    {
        Pill.Visibility = expanded ? Visibility.Hidden : Visibility.Visible;
        Shadow.Visibility = expanded || _style.Shadow == "none" ? Visibility.Hidden : Visibility.Visible;
        if (expanded) return;
        UpdateVisibility();
        if (_style.Peek && !_hovered) SetPeek(true);
    }

    void UpdateVisibility()
    {
        var covered = _style.HideFullscreen && App.Fullscreen?.IsCovered(Screen.HMonitor) == true;
        Visibility = covered ? Visibility.Hidden : Visibility.Visible;
        UpdatePageTimer();
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

    // ---------- peek: a thin bar until the mouse comes near ----------

    void SetPeek(bool peek, bool animate = true)
    {
        if (peek && (!_style.Peek || _hovered || _menuOpen || _inMoment || (_inCall && _style.CallKeepsOpen))) return;
        if (peek == _peeking) return;
        _peeking = peek;
        var width = peek ? PeekWidth : _style.Width;
        var height = peek ? PeekHeight : _style.Height;
        var duration = TimeSpan.FromMilliseconds(animate ? 260 : 0);
        IEasingFunction ease = peek ? new CubicEase { EasingMode = EasingMode.EaseInOut } : new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 };
        foreach (var b in new[] { Pill, Shadow })
        {
            b.BeginAnimation(WidthProperty, new DoubleAnimation(width, duration) { EasingFunction = ease });
            b.BeginAnimation(HeightProperty, new DoubleAnimation(height, duration) { EasingFunction = ease });
            b.CornerRadius = new CornerRadius(peek ? PeekHeight / 2 : _style.CornerRadius);
        }
        var fade = new DoubleAnimation(peek ? 0 : 1, TimeSpan.FromMilliseconds(peek ? 90 : 180)) { BeginTime = peek ? TimeSpan.Zero : TimeSpan.FromMilliseconds(animate ? 120 : 0) };
        Contents.BeginAnimation(OpacityProperty, fade);
        Dots.BeginAnimation(OpacityProperty, fade);
        Contents.IsHitTestVisible = !peek;
    }

    // ---------- pages ----------

    void BuildPages()
    {
        // Music: art · title / artist · bars · ⏮ ⏯ ⏭
        _art.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        _art.Child = Glyph("", 12);
        var texts = Stack(_musicTitle, _musicArtist);
        texts.Margin = new Thickness(9, 0, 6, 0);
        _musicControls.Children.Add(IslandButton(Glyph("", 11), "Previous", () => _ = _media.PreviousAsync()));
        _musicControls.Children.Add(IslandButton(_playGlyph, "Play / pause", () => _ = _media.PlayPauseAsync()));
        _musicControls.Children.Add(IslandButton(Glyph("", 11), "Next", () => _ = _media.NextAsync()));
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < 4; i++)
        {
            var bar = new Rectangle { Width = 3, Height = 14, RadiusX = 1, RadiusY = 1, Margin = new Thickness(1, 0, 1, 0), VerticalAlignment = VerticalAlignment.Bottom,
                RenderTransformOrigin = new Point(0.5, 1), RenderTransform = new ScaleTransform(1, 0.25) };
            bar.SetResourceReference(Shape.FillProperty, "AccentBrush");
            _bars.Children.Add(bar);
        }
        right.Children.Add(_bars);
        right.Children.Add(_musicControls);
        Columns(_musicPage, _art, texts, right);

        // Notifications: icon · app and title / text · count
        var bell = new Grid { Width = 26, Height = 26 };
        bell.Children.Add(Glyph("", 13));
        bell.Children.Add(_notificationIcon);
        var noteTexts = Stack(_notificationTitle, _notificationBody);
        noteTexts.Margin = new Thickness(9, 0, 6, 0);
        _countChip.MinWidth = 20;
        _countChip.Height = 20;
        _countChip.CornerRadius = new CornerRadius(10);
        _countChip.Padding = new Thickness(6, 0, 6, 0);
        _countChip.Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        _countChip.Child = _notificationCount;
        _countChip.VerticalAlignment = VerticalAlignment.Center;
        _notificationCount.HorizontalAlignment = HorizontalAlignment.Center;
        Columns(_notificationsPage, bell, noteTexts, _countChip);
        _notificationsPage.Background = Brushes.Transparent;
        _notificationsPage.Cursor = Cursors.Hand;
        _notificationsPage.ToolTip = "Open Windows' notification centre";

        // Open windows and pinned programs: rows of icons that scroll sideways if there are many
        _windowsScroll.Content = new ItemsControl
        {
            ItemsSource = _windows,
            ItemTemplate = (DataTemplate)Resources["IslandTaskTemplate"],
            ItemsPanel = (ItemsPanelTemplate)FindResource("HorizontalPanel"),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _windowsPage.Children.Add(_windowsScroll);
        _windowsPage.Children.Add(_noWindows);

        _programsScroll.Content = new ItemsControl
        {
            ItemsSource = DockPins.Items,
            ItemTemplate = (DataTemplate)Resources["IslandPinTemplate"],
            ItemsPanel = (ItemsPanelTemplate)FindResource("HorizontalPanel"),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _programsPage.Children.Add(_programsScroll);
        _programsPage.Children.Add(_noPrograms);
        UpdateEmptyStates();

        // Voice call: channel / server · the people in it, speakers glowing
        var headset = new Grid { Width = 26, Height = 26 };
        headset.Children.Add(Glyph("\uE7F6", 13));
        var callTexts = Stack(_callChannel, _callServer);
        callTexts.Margin = new Thickness(6, 0, 8, 0);
        var people = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _callMore.Margin = new Thickness(4, 0, 2, 0);
        people.Children.Add(_callPeople);
        people.Children.Add(_callMore);
        Columns(_callPage, headset, callTexts, people);

        // Clock: time · date
        _clockTime.FontFamily = (FontFamily)FindResource("DisplayFont");
        _clockDate.Margin = new Thickness(10, 3, 0, 0);
        _clockPage.Children.Add(_clockTime);
        _clockPage.Children.Add(_clockDate);
    }

    string CurrentPage => _pages[_page];

    /// <summary>The chosen pages, with the voice call first while you're in one. Keeps <paramref name="keep"/> showing if it's still there.</summary>
    void RebuildPages(string? keep)
    {
        _pages = _style.Pages.Where(p => PageNames.ContainsKey(p) && p != "call").Distinct().ToList();
        if (_pages.Count == 0) _pages = ["windows"];
        if (_inCall) _pages.Insert(0, "call");
        ShowPage(Math.Max(0, keep == null ? 0 : _pages.IndexOf(keep)), 0);
    }

    UIElement PageContent(string page) => page switch
    {
        "notifications" => _notificationsPage,
        "music" => _musicPage,
        "windows" => _windowsPage,
        "clock" => _clockPage,
        "call" => _callPage,
        _ => _programsPage
    };

    void ShowPage(int page, int direction)
    {
        _page = (page % _pages.Count + _pages.Count) % _pages.Count;
        PageHost.Content = PageContent(CurrentPage);
        PrevButton.ToolTip = PageNames[_pages[(_page + _pages.Count - 1) % _pages.Count]];
        NextButton.ToolTip = PageNames[_pages[(_page + 1) % _pages.Count]];
        UpdateDots();
        RefreshPage();
        UpdatePageTimer();
        UpdateBars();
        if (direction == 0) return;

        // Slide the new page in from the side it came from
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(200);
        PageShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(28 * direction, 0, duration) { EasingFunction = ease });
        PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
    }

    bool ShowPage(string id)
    {
        var index = _pages.IndexOf(id);
        if (index < 0 || index == _page) return false;
        ShowPage(index, index > _page ? 1 : -1);
        return true;
    }

    void UpdateDots()
    {
        Dots.Visibility = _style.PageDots && _pages.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        Dots.Children.Clear();
        for (var i = 0; i < _pages.Count; i++)
            Dots.Children.Add(new Ellipse
            {
                Width = 4, Height = 4, Margin = new Thickness(2, 0, 2, 0),
                Fill = new SolidColorBrush(i == _page ? Colors.White : Color.FromArgb(0x47, 0xFF, 0xFF, 0xFF))
            });
    }

    void RefreshPage()
    {
        if (CurrentPage == "notifications") _ = RefreshNotificationsAsync();
        else if (CurrentPage == "clock")
        {
            var now = DateTime.Now;
            _clockTime.Text = now.ToString("t", System.Globalization.CultureInfo.CurrentCulture);
            _clockDate.Text = now.ToString("dddd d MMMM", System.Globalization.CultureInfo.CurrentCulture);
        }
    }

    void UpdatePageTimer()
    {
        var needsTimer = IsVisible && _pages.Count > 0 && CurrentPage is "notifications" or "clock";
        if (needsTimer) _pageTimer.Start(); else _pageTimer.Stop();
    }

    void Flip(int direction)
    {
        if (_inMoment) EndMoment(restorePage: false);
        _pageBeforeMoment = null;
        ShowPage(_page + direction, direction);
        App.Settings.IslandPage = CurrentPage;
        App.Settings.Save();
    }

    void Prev_Click(object sender, RoutedEventArgs e) => Flip(-1);
    void Next_Click(object sender, RoutedEventArgs e) => Flip(1);

    void UpdateEmptyStates()
    {
        _noWindows.Visibility = _windows is { IsEmpty: false } ? Visibility.Collapsed : Visibility.Visible;
        _noPrograms.Visibility = DockPins.Items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------- music ----------

    void OnMediaChanged() => Dispatcher.InvokeAsync(() => _ = RefreshMusicAsync(announce: true));

    async Task RefreshMusicAsync(bool announce)
    {
        var now = await _media.GetAsync();
        _musicTitle.Text = now?.Title ?? "Nothing playing";
        _musicArtist.Text = now?.Artist ?? "";
        _musicArtist.Visibility = _style.MusicArtist && _musicArtist.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _playGlyph.Text = now?.IsPlaying == true ? "" : "";
        if (now?.Art != null)
        {
            _art.Background = new ImageBrush(now.Art) { Stretch = Stretch.UniformToFill };
            _art.Child = null;
        }
        else
        {
            _art.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
            _art.Child ??= Glyph("", 12);
        }

        // A song starting (a new track, or play after pause) is a moment
        var track = now == null ? null : $"{now.Title}|{now.Artist}";
        var started = now?.IsPlaying == true && (track != _lastTrack || !_lastPlaying);
        _lastTrack = track;
        _lastPlaying = now?.IsPlaying == true;
        UpdateBars();
        if (announce && started && _style.JumpToMusic && !GameMode.Active) StartMoment("music", null);
    }

    /// <summary>The little bars bounce while a song plays on the music page.</summary>
    void UpdateBars()
    {
        var run = _style.MusicBars && _lastPlaying && _pages.Count > 0 && CurrentPage == "music" && IsVisible;
        _bars.Visibility = _style.MusicBars ? Visibility.Visible : Visibility.Collapsed;
        var i = 0;
        foreach (var bar in _bars.Children.OfType<Rectangle>())
        {
            var scale = (ScaleTransform)bar.RenderTransform;
            if (!run)
            {
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleY = 0.25;
                continue;
            }
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(420 + 90 * i))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromMilliseconds(-110 * i),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
            i++;
        }
    }

    // ---------- notifications ----------

    async Task RefreshNotificationsAsync()
    {
        List<ToastItem> items;
        try { items = await NotificationReader.ReadAsync(30); }
        catch (Exception ex)
        {
            Log.Error("Island couldn't read notifications", ex);
            return;
        }
        var latest = items.FirstOrDefault();
        _notificationCount.Text = items.Count.ToString();
        _countChip.Visibility = _style.NotificationCount && items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (latest == null)
        {
            _notificationIcon.Source = null;
            _notificationTitle.Text = "No notifications";
            _notificationBody.Text = "";
            _notificationBody.Visibility = Visibility.Collapsed;
            return;
        }
        var app = _apps.Find(latest.AppId);
        _notificationIcon.Source = app?.Icon;
        _notificationTitle.Text = app != null ? $"{app.Name} · {latest.Title}" : latest.Title;
        _notificationBody.Text = latest.Body;
        _notificationBody.Visibility = _style.NotificationText && latest.Body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnNotification(ToastItem item)
    {
        if (!IsVisible || GameMode.Active || !(_style.GrowForNotifications || _style.JumpToNotifications)) return;
        StartMoment(_style.JumpToNotifications ? "notifications" : null, _style.GrowForNotifications ? item : null);
    }

    // ---------- moments: react, then settle back ----------

    /// <summary>Shows <paramref name="page"/> (if it's one of the pages) and/or grows to show <paramref name="toast"/>, for a few seconds.</summary>
    void StartMoment(string? page, ToastItem? toast)
    {
        if (Pill.Visibility != Visibility.Visible) return; // the shell is open
        if (!_inMoment) _pageBeforeMoment = CurrentPage;
        _inMoment = true;
        SetPeek(false);
        if (page != null) ShowPage(page);

        if (toast != null)
        {
            var app = _apps.Find(toast.AppId);
            MomentIcon.Source = app?.Icon;
            MomentTitle.Text = app != null ? $"{app.Name} · {toast.Title}" : toast.Title;
            MomentBody.Text = toast.Body;
            MomentBody.Visibility = toast.Body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            Moment.Visibility = Visibility.Visible;
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 };
            foreach (var b in new[] { Pill, Shadow })
            {
                b.BeginAnimation(HeightProperty, new DoubleAnimation(_style.Height + MomentGrow, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
                b.BeginAnimation(WidthProperty, new DoubleAnimation(_style.Width, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
                b.CornerRadius = new CornerRadius(Math.Min(_style.CornerRadius, (_style.Height + MomentGrow) / 2));
            }
            Contents.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(100)));
            Contents.IsHitTestVisible = false;
            Moment.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)) { BeginTime = TimeSpan.FromMilliseconds(120) });
        }

        _momentTimer.Stop();
        _momentTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, _style.MomentSeconds));
        _momentTimer.Start();
    }

    void EndMoment() => EndMoment(restorePage: true);

    void EndMoment(bool restorePage)
    {
        _momentTimer.Stop();
        if (!_inMoment) return;
        _inMoment = false;
        if (Moment.Visibility == Visibility.Visible)
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(100));
            fade.Completed += (_, _) => { if (!_inMoment) Moment.Visibility = Visibility.Collapsed; };
            Moment.BeginAnimation(OpacityProperty, fade);
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
            foreach (var b in new[] { Pill, Shadow })
            {
                b.BeginAnimation(HeightProperty, new DoubleAnimation(_style.Height, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
                b.CornerRadius = new CornerRadius(_style.CornerRadius);
            }
            Contents.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { BeginTime = TimeSpan.FromMilliseconds(140) });
            Contents.IsHitTestVisible = true;
        }
        if (restorePage && _pageBeforeMoment != null) ShowPage(_pageBeforeMoment);
        _pageBeforeMoment = null;
        if (_style.Peek && !_hovered) _peekTimer.Start();
    }

    // ---------- Discord voice call ----------

    sealed class PeerView
    {
        public required Grid Root;
        public required Ellipse Ring;
        public required Ellipse Photo;
        public required TextBlock Initial;
        public required Border Badge;
        public string? Avatar;
        public bool Speaking;
    }

    void OnCallChanged()
    {
        var inCall = _style.ShowCalls && DiscordVoice.Current != null;
        UpdateCallPage();
        if (inCall == _inCall) return;

        var current = CurrentPage;
        _inCall = inCall;
        if (inCall)
        {
            RebuildPages(current);
            SetPeek(false);
            if (_style.JumpToCall && Pill.Visibility == Visibility.Visible)
            {
                _pageBeforeCall = current;
                ShowPage("call");
            }
        }
        else
        {
            RebuildPages(current == "call" ? _pageBeforeCall : current);
            _pageBeforeCall = null;
            if (_style.Peek && !_hovered) _peekTimer.Start();
        }
    }

    /// <summary>Shows the call's channel and people; as many avatars as fit, then "+N".</summary>
    void UpdateCallPage()
    {
        var call = DiscordVoice.Current;
        if (call == null) return;
        _callChannel.Text = call.Channel;
        _callServer.Text = call.Server ?? "";
        _callServer.Visibility = string.IsNullOrEmpty(call.Server) ? Visibility.Collapsed : Visibility.Visible;
        _callPage.ToolTip = $"{call.Channel}{(call.Server != null ? " · " + call.Server : "")}\n" + string.Join("\n", call.Peers.Select(p => p.Name));

        // Room for avatars: the content area minus the headset glyph and some text
        var room = Math.Max(26, _style.Width - 2 * 33 - 26 - 70);
        var fit = Math.Max(1, (int)(room / 26));
        var shown = call.Peers.Count > fit ? call.Peers.Take(fit - 1).ToList() : call.Peers.ToList();
        _callMore.Text = call.Peers.Count > shown.Count ? $"+{call.Peers.Count - shown.Count}" : "";
        _callMore.Visibility = _callMore.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var gone in _peerViews.Keys.Except(call.Peers.Select(p => p.Id)).ToList()) _peerViews.Remove(gone);
        _callPeople.Children.Clear();
        foreach (var peer in shown)
        {
            if (!_peerViews.TryGetValue(peer.Id, out var view)) _peerViews[peer.Id] = view = MakePeerView();
            UpdatePeerView(view, peer);
            _callPeople.Children.Add(view.Root);
        }
    }

    PeerView MakePeerView()
    {
        var ring = new Ellipse { Width = 26, Height = 26, StrokeThickness = 2, Opacity = 0 };
        ring.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        ring.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Accent(), BlurRadius = 10, ShadowDepth = 0, Opacity = 0.95 };
        var photo = new Ellipse { Width = 20, Height = 20, Fill = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)) };
        var initial = Line(10, bold: true);
        initial.HorizontalAlignment = HorizontalAlignment.Center;
        var badge = new Border
        {
            Width = 10, Height = 10, CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)), BorderBrush = Brushes.Black, BorderThickness = new Thickness(1),
            Child = Glyph("\uE720", 6), Visibility = Visibility.Collapsed
        };
        ((TextBlock)badge.Child).Foreground = Brushes.White;
        var root = new Grid { Width = 26, Height = 26, Margin = new Thickness(0, 0, 2, 0) };
        root.Children.Add(ring);
        root.Children.Add(photo);
        root.Children.Add(initial);
        root.Children.Add(badge);
        return new PeerView { Root = root, Ring = ring, Photo = photo, Initial = initial, Badge = badge };
    }

    void UpdatePeerView(PeerView view, VoicePeer peer)
    {
        if (view.Avatar != peer.Avatar)
        {
            view.Avatar = peer.Avatar;
            var image = peer.Avatar != null ? Avatar(peer.Avatar) : null;
            view.Photo.Fill = image != null ? new ImageBrush(image) { Stretch = Stretch.UniformToFill } : new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        }
        view.Initial.Text = view.Avatar == null && peer.Name.Length > 0 ? peer.Name[..1].ToUpperInvariant() : "";
        view.Badge.Visibility = peer.Muted || peer.Deafened ? Visibility.Visible : Visibility.Collapsed;
        ((TextBlock)view.Badge.Child).Text = peer.Deafened ? "\uE74F" : "\uE720";
        view.Root.ToolTip = peer.Name + (peer.Deafened ? " · deafened" : peer.Muted ? " · muted" : "");
        if (view.Speaking == peer.Speaking) return;
        view.Speaking = peer.Speaking;
        // The glow: a ring in the accent colour fades in while they talk
        view.Ring.BeginAnimation(OpacityProperty, new DoubleAnimation(peer.Speaking ? 1 : 0, TimeSpan.FromMilliseconds(peer.Speaking ? 90 : 250)));
    }

    /// <summary>Discord avatars, downloaded once and kept for the session.</summary>
    static ImageSource? Avatar(string url)
    {
        if (AvatarCache.TryGetValue(url, out var cached)) return cached;
        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(url);
            image.DecodePixelWidth = 64;
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.EndInit();
            AvatarCache[url] = image;
            return image;
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't load a Discord avatar", ex);
            return null;
        }
    }

    // ---------- pinned programs ----------

    void Pin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DockPinItem item) DockPins.Activate(item);
    }

    void Pin_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu, DataContext: DockPinItem item }) PinMenu.Fill(menu, item);
        else e.Handled = true;
    }

    void Pill_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    void Pill_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            DockPins.PinPaths(paths);
            ShowPage("programs");
        }
        e.Handled = true;
    }

    // ---------- the pill itself ----------

    DispatcherTimer? _singleClick;

    /// <summary>
    /// Double-click anywhere on the pill except its buttons opens the shell (when that's the chosen
    /// way). Caught on the way down (preview), since the icon rows' scroll areas swallow ordinary
    /// mouse-down events.
    /// </summary>
    void Pill_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_style.OpenWith != "double" || e.ClickCount != 2 || IsOnButton(e.OriginalSource as DependencyObject)) return;
        _singleClick?.Stop();
        e.Handled = true;
        App.Instance.OpenShellFrom(this);
    }

    /// <summary>A single click: opens the shell (when that's the chosen way), or Windows' notification centre on that page.</summary>
    void Pill_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsOnButton(e.OriginalSource as DependencyObject)) return;
        if (_inMoment && Moment.Visibility == Visibility.Visible)
        {
            EndMoment(restorePage: false);
            ShellHelper.ShowNotificationCenter();
            return;
        }
        var onNotifications = CurrentPage == "notifications" && IsInside(e.OriginalSource as DependencyObject, _notificationsPage);
        if (_style.OpenWith == "single" && !onNotifications)
        {
            App.Instance.OpenShellFrom(this);
            return;
        }
        if (!onNotifications) return;
        // Wait out a possible double-click (which opens the shell) before opening the notification centre.
        _singleClick?.Stop();
        var delay = _style.OpenWith == "double" ? Native.GetDoubleClickTime() : 1;
        _singleClick = new DispatcherTimer(TimeSpan.FromMilliseconds(delay), DispatcherPriority.Normal, (t, _) =>
        {
            ((DispatcherTimer)t!).Stop();
            ShellHelper.ShowNotificationCenter();
        }, Dispatcher);
    }

    bool IsOnButton(DependencyObject? element)
    {
        for (var e = element; e != null && e != Pill; e = ParentOf(e))
            if (e is System.Windows.Controls.Primitives.ButtonBase) return true;
        return false;
    }

    static bool IsInside(DependencyObject? element, DependencyObject container)
    {
        for (var e = element; e != null; e = ParentOf(e))
            if (e == container) return true;
        return false;
    }

    static DependencyObject? ParentOf(DependencyObject e) =>
        e is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(e) : LogicalTreeHelper.GetParent(e);

    void Pill_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (_peeking) return;
        // A long row of icons scrolls sideways first (if chosen); otherwise the wheel flips pages.
        var strip = CurrentPage switch { "windows" => _windowsScroll, "programs" => _programsScroll, _ => null };
        if (_style.WheelScrollsIconsFirst && strip != null && strip.ScrollableWidth > 1)
        {
            var target = Math.Clamp(strip.HorizontalOffset - e.Delta / 2.0, 0, strip.ScrollableWidth);
            if (Math.Abs(target - strip.HorizontalOffset) > 0.5)
            {
                strip.ScrollToHorizontalOffset(target);
                return;
            }
        }
        Flip(e.Delta < 0 ? 1 : -1);
    }

    void Pill_MouseEnter(object sender, MouseEventArgs e)
    {
        _hovered = true;
        _peekTimer.Stop();
        SetPeek(false);
        Grow(1 + _style.HoverGrow / 100);
        if (_style.Arrows == "hover") SetArrowOpacity(1);
        if (_style.OpenWith == "hover") _hoverOpenTimer.Start();
    }

    void Pill_MouseLeave(object sender, MouseEventArgs e)
    {
        _hovered = false;
        _hoverOpenTimer.Stop();
        Grow(1);
        if (_style.Arrows == "hover") SetArrowOpacity(0);
        if (_style.Peek) _peekTimer.Start();
    }

    void Grow(double to)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        foreach (var scale in new[] { PillScale, ShadowScale })
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }
    }

    void PillMenu_Opened(object sender, RoutedEventArgs e) => _menuOpen = true;

    void PillMenu_Closed(object sender, RoutedEventArgs e)
    {
        _menuOpen = false;
        if (_style.Peek && !_hovered) _peekTimer.Start();
    }

    void OpenShell_Click(object sender, RoutedEventArgs e) => App.Instance.OpenShellFrom(this);
    void Settings_Click(object sender, RoutedEventArgs e) => App.Instance.ShowSettings();

    // ---------- little builders ----------

    static TextBlock Line(double size, bool bold = false, bool dim = false)
    {
        var t = new TextBlock { FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        if (bold) t.FontWeight = FontWeights.SemiBold;
        t.SetResourceReference(TextBlock.ForegroundProperty, dim ? "TextDimBrush" : "TextBrush");
        return t;
    }

    static TextBlock Glyph(string glyph, double size)
    {
        var t = new TextBlock { Text = glyph, FontSize = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        return t;
    }

    static TextBlock Empty(string text)
    {
        var t = Line(12, dim: true);
        t.Text = text;
        t.HorizontalAlignment = HorizontalAlignment.Center;
        return t;
    }

    static StackPanel Stack(params UIElement[] children)
    {
        var s = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    static void Columns(Grid grid, UIElement left, UIElement middle, UIElement right)
    {
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(middle, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(middle);
        grid.Children.Add(right);
    }

    // The margins leave room at both ends of a full row to double-click the pill.
    static ScrollViewer IconStrip() => new()
    {
        Margin = new Thickness(22, 0, 22, 0),
        HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Focusable = false
    };

    Button IslandButton(UIElement content, string tip, Action click)
    {
        var b = new Button { Style = (Style)Resources["IslandButton"], Content = content, ToolTip = tip };
        b.Click += (_, _) => click();
        return b;
    }
}
