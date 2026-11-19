using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DeskShell.Services;
using DeskShell.WindowManagement;
using ManagedShell.AppBar;
using ManagedShell.Common.Helpers;

namespace DeskShell;

/// <summary>One app, pinned item or game in the drawer. Its icon loads when it first scrolls into view.</summary>
public sealed class DrawerCell : INotifyPropertyChanged
{
    readonly Func<ImageSource?>? _loadIcon;
    ImageSource? _icon;
    bool _asked, _selected;

    public DrawerCell(string name, Action launch, ImageSource? icon = null, Func<ImageSource?>? loadIcon = null)
    {
        Name = name;
        Launch = launch;
        _icon = icon;
        _loadIcon = loadIcon;
    }

    public string Name { get; }
    public Action Launch { get; }
    /// <summary>Its AppsFolder id, for apps (pin, unpin).</summary>
    public string? AppTarget { get; init; }
    public DockPinItem? Pin { get; init; }
    public int Row { get; set; }

    public ImageSource? Icon
    {
        get
        {
            if (_icon == null && !_asked && _loadIcon != null)
            {
                _asked = true;
                // After input and drawing, so scrolling stays smooth
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    _icon = _loadIcon();
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
                });
            }
            return _icon;
        }
    }

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record DrawerHeader(string Text);

public sealed record DrawerRow(IReadOnlyList<DrawerCell> Cells);

/// <summary>
/// The app drawer, DeskShell's Start menu: search, pinned, recent and every app A–Z, with quick
/// links and power buttons. Opens with the Windows key, the island's button, the shell or its hotkey.
/// Works by keyboard alone: type to search, arrows to move, Enter to open, Esc to close.
/// </summary>
public partial class AppDrawerWindow : Window
{
    const int Columns = 6;

    readonly AppIndex _apps;
    readonly List<object> _rows = new();
    readonly List<DrawerCell> _cells = new();
    int _selected = -1;
    bool _forceClose;
    DispatcherTimer? _confirmTimer;
    string? _armed;

    public bool IsOpen { get; private set; }

    public AppDrawerWindow(AppIndex apps)
    {
        InitializeComponent();
        ThemeManager.Use(Resources);
        _apps = apps;
        Deactivated += (_, _) => CloseDrawer();
        ToolsMenu.Fill(ToolsButton.ContextMenu, CloseDrawer);
        new WindowInteropHelper(this).EnsureHandle();
    }

    // ---------- open / close ----------

    public void Open(AppBarScreen screen)
    {
        if (IsOpen) return;
        IsOpen = true;
        _apps.RefreshIfOld();

        var style = App.Settings.Island;
        var hasIsland = App.Settings.ShowIsland && (App.Settings.IslandOnAllMonitors || screen.Primary);
        _notch = App.Instance.NotchOn(screen);
        var scale = WmNative.ScaleFor(screen.HMonitor);
        var b = screen.Bounds;
        // Out of the notch: flush with the top of the screen. Otherwise below the island.
        var top = _notch != null ? 0 : hasIsland ? style.TopGap + style.Height + 8 : 12;
        var bottomClear = App.Instance.BottomStrip(screen);
        Height = Math.Min(_notch != null ? 740 : 720, b.Height / scale - top - bottomClear - 8);
        SetNotchLook(_notch != null);

        SearchBox.Text = "";
        SearchFilesButton.Visibility = App.Settings.FileSearch ? Visibility.Visible : Visibility.Collapsed;
        DisarmPower();
        ShowBrowse();

        Panel.Opacity = 0;
        Show();
        var hwnd = new WindowInteropHelper(this).Handle;
        var w = (int)Math.Round(Width * scale);
        var h = (int)Math.Round(Height * scale);
        // Twice: the first move may land on a monitor with a different scale and trigger a DPI resize.
        for (var i = 0; i < 2; i++)
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, b.X + (b.Width - w) / 2, b.Y + (int)Math.Round(top * scale), w, h, Native.SWP_SHOWWINDOW);

        WmNative.Activate(hwnd);
        Activate();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);

        if (_notch != null)
        {
            // The notch steps aside and the drawer grows out of its place.
            _notch.SetDrawerOpen(true);
            Panel.BeginAnimation(OpacityProperty, null);
            BeginAnimation(MorphProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380))
            {
                EasingFunction = new BackEase { Amplitude = 0.12, EasingMode = EasingMode.EaseOut }
            });
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(190);
        Panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
        PanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, duration) { EasingFunction = ease });
        PanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.9, 1, duration) { EasingFunction = ease });
    }

    public void CloseDrawer()
    {
        if (!IsOpen) return;
        IsOpen = false;
        if (_notch != null)
        {
            // Shrink back into the notch, then hand the top of the screen back to it.
            var notch = _notch;
            var shrink = new DoubleAnimation(0, TimeSpan.FromMilliseconds(230)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
            shrink.Completed += (_, _) =>
            {
                if (IsOpen) return; // opened again meanwhile
                FinishClosing();
                notch.SetDrawerOpen(false);
            };
            BeginAnimation(MorphProperty, shrink);
            return;
        }
        FinishClosing();
    }

    void FinishClosing()
    {
        Hide();
        Rows.ItemsSource = null;
        _rows.Clear();
        _cells.Clear();
        _selected = -1;
        App.Instance.TrimMemorySoon();
    }

    // ---------- growing out of the notch ----------

    Notch.NotchWindow? _notch;
    System.Windows.Media.Effects.Effect? _shadow;

    public static readonly DependencyProperty MorphProperty = DependencyProperty.Register("Morph", typeof(double), typeof(AppDrawerWindow),
        new PropertyMetadata(0.0, (d, _) => ((AppDrawerWindow)d).LayoutMorph()));

    /// <summary>With the notch, the drawer is the notch grown big: flush with the top, flaring into it, no floating card.</summary>
    void SetNotchLook(bool notch)
    {
        _shadow ??= Panel.Effect;
        Morph.Visibility = notch ? Visibility.Visible : Visibility.Collapsed;
        Panel.Effect = notch ? null : _shadow;
        Panel.Margin = notch ? new Thickness(14 + Notch.NotchWindow.Ear, 0, 14 + Notch.NotchWindow.Ear, 24) : new Thickness(14, 0, 14, 24);
        Panel.Padding = notch ? new Thickness(18, 14, 18, 14) : new Thickness(18, 18, 18, 14);
        Panel.BorderThickness = new Thickness(notch ? 0 : 1);
        if (notch) Panel.Background = Brushes.Transparent;
        else Panel.SetResourceReference(Border.BackgroundProperty, "IslandBrush");
        Panel.Clip = null;
        PanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PanelScale.ScaleX = PanelScale.ScaleY = 1;
    }

    /// <summary>The shape for the current step between the small notch (0) and the open drawer (1).</summary>
    void LayoutMorph()
    {
        if (_notch == null) return;
        var p = (double)GetValue(MorphProperty);
        const double ear = Notch.NotchWindow.Ear;
        var bigW = Width - Panel.Margin.Left - Panel.Margin.Right;
        var bigH = Height - Panel.Margin.Bottom;
        var w = Notch.NotchWindow.SmallW + (bigW - Notch.NotchWindow.SmallW) * p;
        var h = Notch.NotchWindow.SmallH + (bigH - Notch.NotchWindow.SmallH) * p;
        var r = Notch.NotchWindow.SmallR + (28 - Notch.NotchWindow.SmallR) * Math.Clamp(p, 0, 1);
        var left = (Width - (w + 2 * ear)) / 2;
        Morph.Data = Notch.NotchShape.Fill(left, w, h, ear, r);
        // The contents fade in once there's room for them (and out first when closing), inside the shape.
        Panel.Opacity = IsOpen ? Math.Clamp((p - 0.55) / 0.45, 0, 1) : Math.Clamp((p - 0.8) / 0.2, 0, 1);
        Panel.Clip = Notch.NotchShape.Fill(left - Panel.Margin.Left, w, h, ear, r);
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
            CloseDrawer();
        }
        base.OnClosing(e);
    }

    // ---------- contents ----------

    /// <summary>Pinned, recent, then everything A–Z.</summary>
    void ShowBrowse()
    {
        BeginRows();

        var pinned = DockPins.Items.Select(item => new DrawerCell(item.Name, () => DockPins.Activate(item), item.Icon) { Pin = item }).ToList();
        AddSection("Pinned", pinned);

        var all = _apps.AllApps();
        var byTarget = all.ToDictionary(a => a.Target, StringComparer.OrdinalIgnoreCase);
        var recent = App.Settings.RecentApps
            .Where(byTarget.ContainsKey)
            .Select(t => AppCell(byTarget[t].Name, t))
            .ToList();
        AddSection("Recent", recent);

        // Uninstallers clutter the list; they still come up when searched for.
        var letters = all.Where(a => !a.Name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase))
            .GroupBy(a => char.IsLetter(a.Name[0]) ? char.ToUpperInvariant(a.Name[0]) : '#')
            .OrderBy(g => g.Key);
        foreach (var group in letters)
            AddSection(group.Key.ToString(), group.Select(a => AppCell(a.Name, a.Target)).ToList());

        FinishRows(select: false);
    }

    void ShowSearch(string query)
    {
        BeginRows();
        // Games first (they launch through their own launcher), then apps, without listing a game twice.
        var games = GameLibrary.Search(query, 6);
        var gameNames = games.Select(g => Normalize(g.Name)).ToHashSet();
        var cells = games.Select(g => new DrawerCell(g.Name, () => GameLibrary.Launch(g), GameLibrary.Picture(g, out _))).ToList();
        cells.AddRange(_apps.SearchNames(query, 36).Where(a => !gameNames.Contains(Normalize(a.Name))).Select(a => AppCell(a.Name, a.Target)));
        AddSection(null, cells);
        FinishRows(select: true);
    }

    static string Normalize(string name) => new(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    DrawerCell AppCell(string name, string target) => new(name, () =>
    {
        App.Settings.RecordRecentApp(target);
        AppIndex.Launch(target);
    }, loadIcon: () => _apps.IconFor(target)) { AppTarget = target };

    void BeginRows()
    {
        Rows.ItemsSource = null;
        _rows.Clear();
        _cells.Clear();
        _selected = -1;
    }

    void AddSection(string? title, List<DrawerCell> cells)
    {
        if (cells.Count == 0) return;
        if (title != null) _rows.Add(new DrawerHeader(title));
        for (var i = 0; i < cells.Count; i += Columns)
        {
            var row = cells.Skip(i).Take(Columns).ToList();
            foreach (var cell in row) cell.Row = _rows.Count;
            _rows.Add(new DrawerRow(row));
            _cells.AddRange(row);
        }
    }

    void FinishRows(bool select)
    {
        Rows.ItemsSource = _rows;
        NoResults.Visibility = _cells.Count == 0 && SearchBox.Text.Trim().Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoResults.Text = App.Settings.FileSearch ? "No apps match that. Press Enter to search your files for it." : "No apps match that";
        if (_rows.Count > 0) Rows.ScrollIntoView(_rows[0]);
        if (select && _cells.Count > 0) Select(0);
    }

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsOpen) return;
        var query = SearchBox.Text.Trim();
        if (query.Length == 0) ShowBrowse();
        else ShowSearch(query);
    }

    // ---------- keyboard ----------

    void Select(int index)
    {
        if (_cells.Count == 0) return;
        index = Math.Clamp(index, 0, _cells.Count - 1);
        if (_selected >= 0 && _selected < _cells.Count) _cells[_selected].IsSelected = false;
        _selected = index;
        _cells[index].IsSelected = true;
        Rows.ScrollIntoView(_rows[_cells[index].Row]);
    }

    /// <summary>Moves up or down a row, keeping the column where the row is long enough.</summary>
    void MoveRows(int by)
    {
        if (_cells.Count == 0) return;
        if (_selected < 0)
        {
            Select(0);
            return;
        }
        var rowCells = _rows.OfType<DrawerRow>().Select(r => r.Cells).ToList();
        var current = _cells[_selected];
        var rowIndex = rowCells.FindIndex(c => c.Contains(current));
        var column = rowCells[rowIndex].ToList().IndexOf(current);
        var target = rowCells[Math.Clamp(rowIndex + by, 0, rowCells.Count - 1)];
        Select(_cells.IndexOf(target[Math.Min(column, target.Count - 1)]));
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (SearchBox.Text.Length > 0) SearchBox.Text = "";
                else CloseDrawer();
                break;
            // Alt+Enter, or Enter when no app matches: look for files by that name instead.
            case Key.Enter or Key.System when (e.Key == Key.Enter && _selected < 0 || e.SystemKey == Key.Enter) && SearchBox.Text.Trim().Length > 0 && App.Settings.FileSearch:
                SearchFiles(SearchBox.Text.Trim());
                break;
            case Key.Enter when _selected >= 0:
                // Ctrl+Shift+Enter runs it as administrator, as in the Start menu
                if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && _cells[_selected].AppTarget is { } target && AppIndex.CanRunAsAdmin(target)) RunAsAdmin(target);
                else Run(_cells[_selected]);
                break;
            case Key.Down:
                MoveRows(1);
                break;
            case Key.Up:
                MoveRows(-1);
                break;
            case Key.PageDown:
                MoveRows(4);
                break;
            case Key.PageUp:
                MoveRows(-4);
                break;
            case Key.Right when SearchBox.CaretIndex == SearchBox.Text.Length:
                Select(_selected + 1);
                break;
            case Key.Left when _selected > 0 && SearchBox.CaretIndex == SearchBox.Text.Length:
                Select(_selected - 1);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // ---------- apps ----------

    void Run(DrawerCell cell)
    {
        CloseDrawer();
        try { cell.Launch(); }
        catch (Exception ex) { Log.Error($"Could not open {cell.Name}", ex); }
    }

    void RunAsAdmin(string target)
    {
        CloseDrawer();
        App.Settings.RecordRecentApp(target);
        AppIndex.RunAsAdmin(target);
    }

    void Cell_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DrawerCell cell) Run(cell);
    }

    void Cell_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu, DataContext: DrawerCell cell })
        {
            e.Handled = true;
            return;
        }
        if (cell.Pin != null)
        {
            PinMenu.Fill(menu, cell.Pin);
            return;
        }
        if (cell.AppTarget is not { } target)
        {
            e.Handled = true; // games have nothing to offer here
            return;
        }

        menu.Items.Clear();
        MenuItem Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
            return item;
        }
        Add("Open", () => Run(cell));
        if (AppIndex.CanRunAsAdmin(target)) Add("Run as administrator", () => RunAsAdmin(target));
        if (DockPins.Contains("app", target))
            Add("Unpin", () =>
            {
                if (DockPins.Items.FirstOrDefault(i => i.Pin.Kind == "app" && string.Equals(i.Pin.Target, target, StringComparison.OrdinalIgnoreCase)) is { } pin)
                    DockPins.Remove(pin);
                Refresh();
            });
        else
            Add("Pin", () =>
            {
                DockPins.Add(new DockPin { Kind = "app", Target = target, Name = cell.Name });
                Refresh();
            });
        Add("Uninstall or change…", () => OpenThing("ms-settings:appsfeatures"));
    }

    void Refresh()
    {
        if (SearchBox.Text.Trim().Length == 0) ShowBrowse();
    }

    // ---------- quick links ----------

    void OpenThing(string target)
    {
        CloseDrawer();
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error($"Could not open {target}", ex); }
    }

    void OpenFolder(string path)
    {
        CloseDrawer();
        App.Instance.OpenFolder(path);
    }

    void Files_Click(object sender, RoutedEventArgs e)
    {
        CloseDrawer();
        App.Instance.OpenFolder(null);
    }

    /// <summary>Search files, with whatever was typed here.</summary>
    void SearchFiles_Click(object sender, RoutedEventArgs e)
    {
        var text = SearchBox.Text.Trim();
        SearchFiles(text.Length > 0 ? text : null);
    }

    void SearchFiles(string? text)
    {
        CloseDrawer();
        App.Instance.OpenFileSearch(text);
    }
    void Documents_Click(object sender, RoutedEventArgs e) => OpenFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
    void WindowsSettings_Click(object sender, RoutedEventArgs e) => OpenThing("ms-settings:");

    void Tools_Click(object sender, RoutedEventArgs e)
    {
        ToolsButton.ContextMenu.PlacementTarget = ToolsButton;
        ToolsButton.ContextMenu.Placement = PlacementMode.Top;
        ToolsButton.ContextMenu.IsOpen = true;
    }

    void Downloads_Click(object sender, RoutedEventArgs e)
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B"); // FOLDERID_Downloads
        var path = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var found) == 0 ? found : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        OpenFolder(path);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, [MarshalAs(UnmanagedType.LPWStr)] out string path);

    // ---------- power ----------

    void Lock_Click(object sender, RoutedEventArgs e)
    {
        CloseDrawer();
        ShellHelper.Lock();
    }

    void Sleep_Click(object sender, RoutedEventArgs e)
    {
        CloseDrawer();
        PowerHelper.Sleep();
    }

    void Restart_Click(object sender, RoutedEventArgs e) => Confirm("restart", PowerHelper.Reboot);
    void Shutdown_Click(object sender, RoutedEventArgs e) => Confirm("shut down", PowerHelper.Shutdown);

    /// <summary>First click arms it for three seconds; a second click does it.</summary>
    void Confirm(string what, Action action)
    {
        if (_armed == what)
        {
            DisarmPower();
            CloseDrawer();
            action();
            return;
        }
        _armed = what;
        PowerPrompt.Text = $"Click again to {what}";
        PowerPrompt.Visibility = Visibility.Visible;
        Links.Visibility = Visibility.Collapsed;
        _confirmTimer?.Stop();
        _confirmTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Normal, (_, _) => DisarmPower(), Dispatcher);
    }

    void DisarmPower()
    {
        _confirmTimer?.Stop();
        _armed = null;
        PowerPrompt.Visibility = Visibility.Collapsed;
        Links.Visibility = Visibility.Visible;
    }
}
