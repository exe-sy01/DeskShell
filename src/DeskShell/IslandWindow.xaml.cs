using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DeskShell.Services;
using ManagedShell;
using ManagedShell.AppBar;
using ManagedShell.Common.Helpers;
using ManagedShell.WindowsTasks;

namespace DeskShell;

/// <summary>
/// The island: a black pill at the top of the screen, a fixed size, with ‹ › to flip between
/// pages (notifications, music, open windows, pinned programs). Double-click expands it into the
/// shell. Hides while a fullscreen or borderless window fills its monitor.
/// </summary>
public partial class IslandWindow : Window
{
    public const double PillWidth = 420, PillHeight = 40, TopGap = 8;
    /// <summary>The strip at the top of the screen kept free for the island.</summary>
    public const double ReservedHeight = TopGap + PillHeight + 6;

    static readonly string[] Pages = ["notifications", "music", "windows", "programs"];
    static readonly string[] PageNames = ["Notifications", "Music", "Open windows", "Programs"];

    readonly ShellManager _shell;
    readonly MediaService _media;
    readonly AppIndex _apps;
    readonly ICollectionView? _windows;
    readonly DispatcherTimer _notificationTimer;
    int _page;
    bool _expanded;
    bool _allowClose;

    // Pages, built once and updated in place
    readonly Grid _musicPage = new();
    readonly Border _art = new() { Width = 26, Height = 26, CornerRadius = new CornerRadius(6) };
    readonly TextBlock _musicTitle = Line(12.5, bold: true);
    readonly TextBlock _musicArtist = Line(11, dim: true);
    readonly TextBlock _playGlyph = Glyph("", 12);
    readonly Grid _notificationsPage = new();
    readonly Image _notificationIcon = new() { Width = 18, Height = 18 };
    readonly TextBlock _notificationTitle = Line(12.5, bold: true);
    readonly TextBlock _notificationBody = Line(11, dim: true);
    readonly TextBlock _notificationCount = Line(11, bold: true);
    readonly Grid _windowsPage = new();
    readonly ScrollViewer _windowsScroll = IconStrip();
    readonly TextBlock _noWindows = Empty("No open windows");
    readonly Grid _programsPage = new();
    readonly ScrollViewer _programsScroll = IconStrip();
    readonly TextBlock _noPrograms = Empty("Right-click a window or app and choose Pin");

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
        _notificationTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, (_, _) => _ = RefreshNotificationsAsync(), Dispatcher);
        _notificationTimer.Stop();
        BuildPages();

        _page = Math.Max(0, Array.IndexOf(Pages, App.Settings.IslandPage));
        ShowPage(_page, 0);

        _media.Changed += OnMediaChanged;
        DockPins.Items.CollectionChanged += (_, _) => UpdateEmptyStates();
        if (_windows is System.Collections.Specialized.INotifyCollectionChanged changes)
            changes.CollectionChanged += (_, _) => UpdateEmptyStates();
        if (App.Fullscreen != null) App.Fullscreen.Changed += UpdateVisibility;
        _ = RefreshMusicAsync();

        SourceInitialized += (_, _) => Native.MakeNonActivating(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => { Place(); UpdateVisibility(); };
        Closed += (_, _) =>
        {
            _media.Changed -= OnMediaChanged;
            _notificationTimer.Stop();
            if (App.Fullscreen != null) App.Fullscreen.Changed -= UpdateVisibility;
        };
    }

    bool IncludeWindow(object item) =>
        item is ApplicationWindow { ShowInTaskbar: true } window &&
        (!App.Settings.IslandOnAllMonitors || window.HMonitor == Screen.HMonitor);

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

    /// <summary>The pill's rectangle on screen, in physical pixels (for the shell's expand animation).</summary>
    public Rect PillScreenRect
    {
        get
        {
            var topLeft = Pill.PointToScreen(new Point(0, 0));
            var bottomRight = Pill.PointToScreen(new Point(Pill.ActualWidth, Pill.ActualHeight));
            return new Rect(topLeft, bottomRight);
        }
    }

    public bool IsShowingPill => IsVisible && Pill.Visibility == Visibility.Visible;

    /// <summary>While the shell is open the island "is" the shell: the pill hides and the shell grows out of its place.</summary>
    public void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        Pill.Visibility = Shadow.Visibility = expanded ? Visibility.Hidden : Visibility.Visible;
        if (!expanded) UpdateVisibility();
    }

    void UpdateVisibility()
    {
        var covered = App.Fullscreen?.IsCovered(Screen.HMonitor) == true;
        Visibility = covered ? Visibility.Hidden : Visibility.Visible;
        if (covered) _notificationTimer.Stop();
        else if (Pages[_page] == "notifications") _notificationTimer.Start();
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

    // ---------- pages ----------

    void BuildPages()
    {
        // Music: art · title / artist · ⏮ ⏯ ⏭
        _art.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        _art.Child = Glyph("", 12);
        var texts = Stack(_musicTitle, _musicArtist);
        texts.Margin = new Thickness(9, 0, 6, 0);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(IslandButton(Glyph("", 11), "Previous", () => _ = _media.PreviousAsync()));
        controls.Children.Add(IslandButton(_playGlyph, "Play / pause", () => _ = _media.PlayPauseAsync()));
        controls.Children.Add(IslandButton(Glyph("", 11), "Next", () => _ = _media.NextAsync()));
        Columns(_musicPage, _art, texts, controls);

        // Notifications: icon · app and title / text · count
        var bell = new Grid { Width = 26, Height = 26 };
        bell.Children.Add(Glyph("", 13));
        bell.Children.Add(_notificationIcon);
        var noteTexts = Stack(_notificationTitle, _notificationBody);
        noteTexts.Margin = new Thickness(9, 0, 6, 0);
        var count = new Border
        {
            MinWidth = 20, Height = 20, CornerRadius = new CornerRadius(10), Padding = new Thickness(6, 0, 6, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), Child = _notificationCount,
            VerticalAlignment = VerticalAlignment.Center
        };
        _notificationCount.HorizontalAlignment = HorizontalAlignment.Center;
        Columns(_notificationsPage, bell, noteTexts, count);
        _notificationsPage.Background = Brushes.Transparent;
        _notificationsPage.Cursor = Cursors.Hand;
        _notificationsPage.ToolTip = "Open Windows' notification centre";
        // A single click opens Windows' notification centre; wait out a possible double-click (which opens the shell).
        _notificationsPage.MouseLeftButtonUp += (_, _) =>
        {
            _singleClick?.Stop();
            _singleClick = new DispatcherTimer(TimeSpan.FromMilliseconds(Native.GetDoubleClickTime()), DispatcherPriority.Normal, (t, _) =>
            {
                ((DispatcherTimer)t!).Stop();
                ShellHelper.ShowNotificationCenter();
            }, Dispatcher);
        };

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
    }

    UIElement PageContent(int page) => Pages[page] switch
    {
        "notifications" => _notificationsPage,
        "music" => _musicPage,
        "windows" => _windowsPage,
        _ => _programsPage
    };

    void ShowPage(int page, int direction)
    {
        _page = (page + Pages.Length) % Pages.Length;
        PageHost.Content = PageContent(_page);
        PrevButton.ToolTip = PageNames[(_page + Pages.Length - 1) % Pages.Length];
        NextButton.ToolTip = PageNames[(_page + 1) % Pages.Length];
        if (Pages[_page] == "notifications")
        {
            _ = RefreshNotificationsAsync();
            if (IsVisible) _notificationTimer.Start();
        }
        else
        {
            _notificationTimer.Stop();
        }
        if (direction == 0) return;

        // Slide the new page in from the side it came from
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(200);
        PageShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(28 * direction, 0, duration) { EasingFunction = ease });
        PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
    }

    void Flip(int direction)
    {
        ShowPage(_page + direction, direction);
        App.Settings.IslandPage = Pages[_page];
        App.Settings.Save();
    }

    void Prev_Click(object sender, RoutedEventArgs e) => Flip(-1);
    void Next_Click(object sender, RoutedEventArgs e) => Flip(1);

    void UpdateEmptyStates()
    {
        _noWindows.Visibility = _windows is { IsEmpty: false } ? Visibility.Collapsed : Visibility.Visible;
        _noPrograms.Visibility = DockPins.Items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    void OnMediaChanged() => Dispatcher.InvokeAsync(() => _ = RefreshMusicAsync());

    async Task RefreshMusicAsync()
    {
        var now = await _media.GetAsync();
        _musicTitle.Text = now?.Title ?? "Nothing playing";
        _musicArtist.Text = now?.Artist ?? "";
        _musicArtist.Visibility = string.IsNullOrEmpty(_musicArtist.Text) ? Visibility.Collapsed : Visibility.Visible;
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
    }

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
        ((FrameworkElement)_notificationCount.Parent).Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        _notificationBody.Visibility = string.IsNullOrEmpty(latest.Body) ? Visibility.Collapsed : Visibility.Visible;
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
            ShowPage(Array.IndexOf(Pages, "programs"), 1);
        }
        e.Handled = true;
    }

    // ---------- the pill itself ----------

    DispatcherTimer? _singleClick;

    /// <summary>
    /// Double-click anywhere on the pill except its buttons opens the shell. Caught on the way down
    /// (preview), since the icon rows' scroll areas swallow ordinary mouse-down events.
    /// </summary>
    void Pill_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || IsOnButton(e.OriginalSource as DependencyObject)) return;
        _singleClick?.Stop();
        e.Handled = true;
        App.Instance.OpenShellFrom(this);
    }

    bool IsOnButton(DependencyObject? element)
    {
        for (var e = element; e != null && e != Pill; e = e is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(e) : LogicalTreeHelper.GetParent(e))
            if (e is System.Windows.Controls.Primitives.ButtonBase) return true;
        return false;
    }

    void Pill_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        // A long row of icons scrolls sideways; otherwise the wheel flips pages.
        var strip = Pages[_page] switch { "windows" => _windowsScroll, "programs" => _programsScroll, _ => null };
        if (strip != null && strip.ScrollableWidth > 1)
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

    void Pill_MouseEnter(object sender, MouseEventArgs e) => Grow(1.025);
    void Pill_MouseLeave(object sender, MouseEventArgs e) => Grow(1);

    void Grow(double to)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        foreach (var scale in new[] { PillScale, ShadowScale })
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }
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
