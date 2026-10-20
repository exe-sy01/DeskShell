using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DeskShell.Services;
using DeskShell.Tiles;
using DeskShell.WindowManagement;
using ManagedShell;
using ManagedShell.Common.Helpers;

namespace DeskShell;

/// <summary>
/// The centered overlay: clock, app search, a grid of tiles you can rearrange, power buttons,
/// and a strip of every open window. Created once and shown/hidden.
/// </summary>
public partial class ShellWindow : Window
{
    readonly ShellContext _context;
    readonly WindowBoard _board;
    readonly DispatcherTimer _clock;
    bool _forceClose;
    bool _editing;
    TileHost? _drag;
    Point _dragStart;
    bool _dragMoved;
    TileHost? _lastSwap;

    public bool IsOpen { get; private set; }

    public ShellWindow(ShellManager shell, AppIndex apps, ClipboardHistory clipboard)
    {
        InitializeComponent();

        var media = new MediaService();
        var toggles = new SystemToggles();
        _context = new ShellContext
        {
            Shell = shell,
            Media = media,
            Toggles = toggles,
            Apps = apps,
            Clipboard = clipboard,
            CloseShell = immediate => CloseShell(immediate),
            BeginAppMenu = BeginAppMenu
        };
        _ = media.StartAsync();
        _ = toggles.StartAsync();

        _board = new WindowBoard(shell, apps);
        LeftHost.Content = _board.Left;
        RightHost.Content = _board.Right;
        BuildTiles();
        UpdateHint();

        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateClock(), Dispatcher);
        _clock.Stop();

        Deactivated += (_, _) => { if (_drag == null && !_appMenuOpen) CloseShell(); };
        new WindowInteropHelper(this).EnsureHandle();
    }

    IEnumerable<TileHost> Hosts => Tiles.Children.OfType<TileHost>();

    public void UpdateHint() =>
        HintText.Text = $"Type to search apps   ·   Esc or {App.Settings.ShellHotkey} to close   ·   right-click a tile for its settings   ·   {App.Settings.QuitHotkey} quits DeskShell";

    // ---------- open / close ----------

    public void OpenShell()
    {
        if (IsOpen) return;
        IsOpen = true;

        var screen = ZoneManager.ScreenAtCursor();
        UpdateClock();
        SearchBox.Text = "";
        foreach (var host in Hosts) SafeCall(host.Tile.OnOpened);
        _ = BadgeService.Instance.RefreshAsync();
        _board.Open(WmNative.GetForegroundWindow());
        var update = UpdateService.Available;
        UpdateButton.Visibility = update != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateButton.Content = update != null ? $"Update to {update.Tag}" : "";
        UpdateButton.IsEnabled = true;

        Backdrop.Opacity = 0;
        Show();
        var hwnd = new WindowInteropHelper(this).Handle;
        var b = screen.Bounds;
        // Twice: the first move may land on a monitor with a different scale and trigger a DPI resize.
        for (var i = 0; i < 2; i++)
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, Native.SWP_SHOWWINDOW);
        FitToScreen();

        Activate();
        Native.SetForegroundWindow(hwnd);
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Backdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        var grow = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
        ContentScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        ContentScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        _clock.Start();
    }

    // ---------- app menus over the shell ----------

    bool _appMenuOpen;
    DispatcherTimer? _menuWatch;

    /// <summary>
    /// An app (e.g. a tray icon) is about to show its own menu. Many of those menus aren't
    /// "always on top", so they'd open behind the shell; drop the shell below them instead and
    /// keep it open while the menu has focus.
    /// </summary>
    void BeginAppMenu()
    {
        _appMenuOpen = true;
        Topmost = false;
        _menuWatch ??= new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Normal, (_, _) => WatchAppMenu(), Dispatcher);
        _menuWatch.Start();
    }

    void WatchAppMenu()
    {
        var foreground = WmNative.GetForegroundWindow();
        var own = new WindowInteropHelper(this).Handle;
        if (foreground == own)
        {
            // Menu closed and you're back in the shell.
            EndAppMenu();
            return;
        }
        if (foreground == IntPtr.Zero || !IsRealWindow(foreground)) return; // menu still open

        // The app opened a window (e.g. "Open Steam"), or you switched apps: get out of the way.
        EndAppMenu();
        CloseShell(immediate: true);
    }

    void EndAppMenu()
    {
        if (!_appMenuOpen) return;
        _appMenuOpen = false;
        _menuWatch?.Stop();
        Topmost = true;
    }

    /// <summary>A normal app window, as opposed to a menu, popup, or an app's hidden helper window.</summary>
    bool IsRealWindow(IntPtr hwnd)
    {
        if (_context.Shell.Tasks.GroupedWindows?.OfType<ManagedShell.WindowsTasks.ApplicationWindow>().Any(w => w.Handle == hwnd) == true)
            return true;
        if (!WmNative.IsWindowVisible(hwnd)) return false;
        var style = WmNative.GetWindowLongPtr(hwnd, -16 /* GWL_STYLE */).ToInt64();
        var exStyle = WmNative.GetWindowLongPtr(hwnd, WmNative.GWL_EXSTYLE).ToInt64();
        const long WS_CAPTION = 0x00C00000;
        return (style & WS_CAPTION) == WS_CAPTION && (exStyle & WmNative.WS_EX_TOOLWINDOW) == 0;
    }

    /// <summary>
    /// The shell stays in the middle. On wide screens Monitor 1's windows sit in a panel to its left
    /// and Monitor 2's to its right; otherwise one panel goes underneath. If it still doesn't fit
    /// the screen, everything shrinks.
    /// </summary>
    void FitToScreen()
    {
        const double sideGap = 16, sideMin = 300, sideMax = 380;
        ShellPanel.LayoutTransform = Transform.Identity;
        // Keep clear of the dock at the bottom.
        var availableHeight = ActualHeight - App.Settings.DockHeight - 40;
        var availableWidth = ActualWidth - 80;

        var sideWidth = Math.Min(sideMax, (availableWidth - Card.Width) / 2 - sideGap);
        if (sideWidth >= sideMin)
        {
            Grid.SetRow(LeftHost, 0);
            Grid.SetColumn(LeftHost, 0);
            LeftHost.Margin = new Thickness(0, 0, sideGap, 0);
            RightHost.Margin = new Thickness(sideGap, 0, 0, 0);
            RightHost.Visibility = Visibility.Visible;
            UpdateLayout();
            _board.Arrange(sides: true, sideWidth, Card.ActualHeight);
        }
        else
        {
            Grid.SetRow(LeftHost, 1);
            Grid.SetColumn(LeftHost, 1);
            LeftHost.Margin = new Thickness(0, 14, 0, 0);
            RightHost.Visibility = Visibility.Collapsed;
            UpdateLayout();
            var hint = HintText.ActualHeight + HintText.Margin.Top;
            _board.Arrange(sides: false, Card.Width, availableHeight - Card.ActualHeight - LeftHost.Margin.Top - hint - 24);
        }

        // If it still doesn't fit (small screens), shrink everything to fit.
        UpdateLayout();
        var scale = Math.Min(1, Math.Min(availableHeight / ShellPanel.ActualHeight, ActualWidth / (ShellPanel.ActualWidth + 40)));
        if (scale < 1 && scale > 0) ShellPanel.LayoutTransform = new ScaleTransform(scale, scale);
    }

    public void CloseShell(bool immediate = false)
    {
        if (!IsOpen) return;
        EndAppMenu();
        IsOpen = false;
        _clock.Stop();
        if (_editing) SetEditing(false);
        foreach (var host in Hosts) SafeCall(host.Tile.OnClosed);
        _board.Close();
        App.Instance.TrimMemorySoon();

        if (immediate)
        {
            Backdrop.BeginAnimation(OpacityProperty, null);
            Backdrop.Opacity = 0;
            Hide();
            return;
        }

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
        fade.Completed += (_, _) => { if (!IsOpen) Hide(); };
        Backdrop.BeginAnimation(OpacityProperty, fade);
    }

    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_forceClose)
        {
            e.Cancel = true;
            CloseShell();
        }
        base.OnClosing(e);
    }

    static void SafeCall(Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Error("A tile failed to update", ex); }
    }

    void UpdateClock()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("t", CultureInfo.CurrentCulture);
        DateText.Text = now.ToString("dddd d MMMM", CultureInfo.CurrentCulture);
    }

    void Backdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == Backdrop) CloseShell();
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_editing) SetEditing(false);
            else if (SearchBox.Text.Length > 0) SearchBox.Text = "";
            else CloseShell();
            e.Handled = true;
            return;
        }

        if (Results.Visibility != Visibility.Visible || Results.Items.Count == 0) return;
        switch (e.Key)
        {
            case Key.Down:
                Results.SelectedIndex = Math.Min(Results.SelectedIndex + 1, Results.Items.Count - 1);
                e.Handled = true;
                break;
            case Key.Up:
                Results.SelectedIndex = Math.Max(Results.SelectedIndex - 1, 0);
                e.Handled = true;
                break;
            case Key.Enter:
                if (Results.SelectedItem is AppResult app) Launch(app);
                e.Handled = true;
                break;
        }
    }

    // ---------- search ----------

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text;
        if (query.Trim().Length == 0)
        {
            Results.Visibility = NoResults.Visibility = Visibility.Collapsed;
            Tiles.Visibility = Visibility.Visible;
            return;
        }

        // Games first (they launch through their own launcher), then apps, without listing a game twice.
        var games = GameLibrary.Search(query, 4)
            .Select(g => new AppResult(g.Name, "game:" + g.Key, GameLibrary.Picture(g, out _)))
            .ToList();
        var gameNames = games.Select(g => Normalize(g.Name)).ToHashSet();
        var results = games
            .Concat(_context.Apps.Search(query).Where(a => !gameNames.Contains(Normalize(a.Name))))
            .Take(8)
            .ToList();
        Results.ItemsSource = results;
        Results.SelectedIndex = results.Count > 0 ? 0 : -1;
        // Hidden (not collapsed) keeps the panel the same size while you type.
        Tiles.Visibility = Visibility.Hidden;
        Results.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoResults.Visibility = results.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    void Results_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemUnder(e.OriginalSource) is { } app) Launch(app);
    }

    void Results_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = Results.ContextMenu!;
        menu.Items.Clear();
        if (ItemUnder(e.OriginalSource) is not { } app || app.Target.StartsWith("game:"))
        {
            e.Handled = true;
            return;
        }
        var pinned = App.Settings.PinnedApps.Any(p => p.Target == app.Target);
        var pin = new MenuItem { Header = pinned ? "Unpin" : "Pin to shell" };
        pin.Click += (_, _) =>
        {
            if (pinned) App.Settings.PinnedApps.RemoveAll(p => p.Target == app.Target);
            else App.Settings.PinnedApps.Add(new PinnedApp { Name = app.Name, Target = app.Target });
            App.Settings.Save();
            foreach (var host in Hosts.Where(h => h.Tile.Id == "pinned")) host.Tile.OnOpened();
        };
        menu.Items.Add(pin);

        var onDock = DockPins.Contains("app", app.Target);
        var dockPin = new MenuItem { Header = onDock ? "Unpin from dock" : "Pin to dock" };
        dockPin.Click += (_, _) =>
        {
            if (onDock)
            {
                if (DockPins.Items.FirstOrDefault(i => i.Pin.Kind == "app" && string.Equals(i.Pin.Target, app.Target, StringComparison.OrdinalIgnoreCase)) is { } item)
                    DockPins.Remove(item);
            }
            else
            {
                DockPins.Add(new DockPin { Kind = "app", Target = app.Target, Name = app.Name });
            }
        };
        menu.Items.Add(dockPin);
    }

    static AppResult? ItemUnder(object source)
    {
        for (var d = source as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is ListBoxItem { DataContext: AppResult app }) return app;
        return null;
    }

    static string Normalize(string name) => new(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    void Launch(AppResult app)
    {
        CloseShell(immediate: true);
        if (app.Target.StartsWith("game:") && GameLibrary.Find(app.Target[5..]) is { } game) GameLibrary.Launch(game);
        else AppIndex.Launch(app.Target);
    }

    // ---------- tiles and customising ----------

    void BuildTiles()
    {
        foreach (var host in Hosts) host.Tile.OnClosed();
        Tiles.Children.Clear();
        foreach (var setting in App.Settings.Tiles)
            AddTile(setting.Id, setting.Width, setting.Height);
    }

    /// <summary>Recreates the tiles (after a theme change), keeping the layout.</summary>
    public void RebuildTiles()
    {
        BuildTiles();
        if (IsOpen)
            foreach (var host in Hosts) SafeCall(host.Tile.OnOpened);
    }

    public void ResetTiles()
    {
        App.Settings.Tiles = Settings.DefaultTiles();
        App.Settings.Save();
        BuildTiles();
    }

    void AddTile(string id, int width, int height)
    {
        if (Hosts.Any(h => h.Tile.Id == id)) return;
        TileBase? tile;
        try { tile = TileFactory.Create(id, _context); }
        catch (Exception ex)
        {
            Log.Error($"Tile {id} could not be created", ex);
            return;
        }
        if (tile == null) return;

        if (!tile.Sizes.Any(s => s.W == width && s.H == height)) (width, height) = tile.Sizes[0];
        var host = new TileHost(tile, width, height) { Editing = _editing };
        host.RemoveRequested += h =>
        {
            h.Tile.OnClosed();
            Tiles.Children.Remove(h);
            BuildAddBar();
            SaveTiles();
        };
        host.Resized += SaveTiles;
        Tiles.Children.Add(host);
    }

    void SaveTiles()
    {
        App.Settings.Tiles = Hosts.Select(h => new TileSetting(h.Tile.Id, h.ColSpan, h.RowSpan)).ToList();
        App.Settings.Save();
    }

    void Customise_Click(object sender, RoutedEventArgs e) => SetEditing(!_editing);

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "Downloading…";
        var problem = await UpdateService.InstallAsync();
        if (problem == null) return; // the new version asks this one to quit
        UpdateButton.Content = problem;
        UpdateButton.IsEnabled = true;
    }

    void SetEditing(bool on)
    {
        _editing = on;
        foreach (var host in Hosts) host.Editing = on;
        CustomiseButton.Content = on ? "Done" : "Customise";
        SearchBox.IsEnabled = !on;
        AddTilesBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) BuildAddBar();
        else SaveTiles();
    }

    void BuildAddBar()
    {
        AddTilesBar.Children.Clear();
        AddTilesBar.Children.Add(new TextBlock
        {
            Text = "Drag tiles to move them. Add:",
            Foreground = (Brush)FindResource("TextDimBrush"),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 10, 6)
        });
        var missing = TileFactory.AllIds.Where(id => Hosts.All(h => h.Tile.Id != id)).ToList();
        if (missing.Count == 0)
        {
            AddTilesBar.Children.Add(new TextBlock { Text = "every tile is already on", Foreground = (Brush)FindResource("TextDimBrush"), FontSize = 12.5, Margin = new Thickness(0, 0, 0, 6) });
            return;
        }
        foreach (var id in missing)
        {
            var chip = new Button { Style = (Style)FindResource("ChipButton"), Content = "+  " + TileFactory.Titles[id], Margin = new Thickness(0, 0, 6, 6) };
            chip.Click += (_, _) =>
            {
                AddTile(id, 0, 0);
                foreach (var host in Hosts.Where(h => h.Tile.Id == id)) host.Tile.OnOpened();
                BuildAddBar();
                SaveTiles();
            };
            AddTilesBar.Children.Add(chip);
        }
    }

    void Tiles_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_editing) return;
        TileHost? host = null;
        for (var d = e.OriginalSource as DependencyObject; d != null && d != Tiles; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ButtonBase) return; // the size / remove buttons
            if (d is TileHost h) { host = h; break; }
        }
        if (host == null) return;
        _drag = host;
        _dragStart = e.GetPosition(Tiles);
        _dragMoved = false;
        _lastSwap = null;
        Tiles.CaptureMouse();
        e.Handled = true;
    }

    void Tiles_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_drag == null) return;
        var pos = e.GetPosition(Tiles);
        if (!_dragMoved && (pos - _dragStart).Length < 6) return;
        _dragMoved = true;
        _drag.Opacity = 0.65;

        var target = Tiles.Slots.FirstOrDefault(s => s.Value.Contains(pos) && s.Key != _drag).Key as TileHost;
        if (target == null)
        {
            _lastSwap = null;
            return;
        }
        if (target == _lastSwap) return; // wait until the pointer leaves the tile we just swapped with
        _lastSwap = target;
        var index = Tiles.Children.IndexOf(target);
        Tiles.Children.Remove(_drag);
        Tiles.Children.Insert(index, _drag);
    }

    void Tiles_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag == null) return;
        Tiles.ReleaseMouseCapture();
        _drag.Opacity = 1;
        _drag = null;
        if (_dragMoved) SaveTiles();
        e.Handled = true;
    }

    // ---------- footer ----------

    void Lock_Click(object sender, RoutedEventArgs e)
    {
        CloseShell(immediate: true);
        ShellHelper.Lock();
    }

    void Sleep_Click(object sender, RoutedEventArgs e)
    {
        CloseShell(immediate: true);
        PowerHelper.Sleep();
    }

    void Restart_Click(object sender, RoutedEventArgs e) => Confirm(RestartButton, "Click again to restart", PowerHelper.Reboot);

    void Shutdown_Click(object sender, RoutedEventArgs e) => Confirm(ShutdownButton, "Click again to shut down", PowerHelper.Shutdown);

    /// <summary>First click arms the button for three seconds; a second click does it.</summary>
    void Confirm(Button button, string prompt, Action action)
    {
        if (Equals(button.Content, prompt))
        {
            CloseShell(immediate: true);
            action();
            return;
        }

        var original = button.Content;
        button.Content = prompt;
        button.Background = (Brush)FindResource("DangerBrush");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            button.Content = original;
            button.ClearValue(BackgroundProperty);
        };
        timer.Start();
    }

    void Overview_Click(object sender, RoutedEventArgs e) => App.Instance.Overview.OpenOverview();

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        CloseShell(immediate: true);
        App.Instance.ShowSettings();
    }

    void Quit_Click(object sender, RoutedEventArgs e) => App.Instance.Quit();
}
