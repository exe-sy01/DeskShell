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
        ThemeManager.AlwaysDark(Resources);
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
        var scale = WmNative.ScaleFor(screen.HMonitor);
        var b = screen.Bounds;
        var top = hasIsland ? style.TopGap + style.Height + 8 : 12;
        var bottomClear = App.Instance.BottomStrip(screen);
        Height = Math.Min(720, b.Height / scale - top - bottomClear - 8);

        SearchBox.Text = "";
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
        Hide();
        Rows.ItemsSource = null;
        _rows.Clear();
        _cells.Clear();
        _selected = -1;
        App.Instance.TrimMemorySoon();
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
