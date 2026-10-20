using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskShell.Services;
using DeskShell.WindowManagement;
using ManagedShell;
using ManagedShell.AppBar;
using ManagedShell.WindowsTasks;

namespace DeskShell;

/// <summary>
/// Every open window, shown under the shell: a column per monitor (left to right), windows grouped
/// by app inside it, and each window as a preview card with its full title. Snapshots are taken in
/// the background when the shell opens, so opening stays instant.
/// </summary>
public sealed class WindowBoard : Border
{
    const double CardWidth = 200, ShotHeight = 112;

    readonly ShellManager _shell;
    readonly AppIndex _apps;
    readonly Grid _columns = new();
    readonly ScrollViewer _scroll;

    /// <summary>How tall the list may get before it scrolls (set by the shell to fit the screen).</summary>
    public double MaxListHeight { set => _scroll.MaxHeight = value; }
    readonly Dictionary<IntPtr, BitmapSource?> _shots = new();
    readonly Dictionary<IntPtr, Border> _shotSlots = new();
    readonly DispatcherTimer _rebuild;
    IntPtr _lastActive;
    int _captureRun;
    bool _open;

    public WindowBoard(ShellManager shell, AppIndex apps)
    {
        _shell = shell;
        _apps = apps;
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(14, 10, 8, 10);
        SetResourceReference(BackgroundProperty, "CardBrush");
        SetResourceReference(BorderBrushProperty, "EdgeBrush");
        _scroll = new ScrollViewer
        {
            Content = _columns,
            MaxHeight = 330,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 6, 0)
        };
        Child = _scroll;

        _rebuild = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) =>
        {
            _rebuild!.Stop();
            if (_open) Build();
        }, Dispatcher);
        _rebuild.Stop();
        if (shell.Tasks.GroupedWindows?.SourceCollection is INotifyCollectionChanged changes)
            changes.CollectionChanged += (_, _) => Dispatcher.InvokeAsync(() => { _rebuild.Stop(); _rebuild.Start(); });
    }

    List<ApplicationWindow> Windows() =>
        _shell.Tasks.GroupedWindows?.SourceCollection?.OfType<ApplicationWindow>().Where(w => w.ShowInTaskbar).ToList() ?? new();

    /// <summary>Called as the shell opens. <paramref name="lastActive"/> is the window you were using, which gets highlighted.</summary>
    public void Open(IntPtr lastActive)
    {
        _open = true;
        _lastActive = lastActive;
        Build();
        _ = CaptureAsync();
    }

    public void Close()
    {
        _open = false;
        _captureRun++; // abandon any capture still running
    }

    // ---------- layout ----------

    void Build()
    {
        _columns.Children.Clear();
        _columns.ColumnDefinitions.Clear();
        _shotSlots.Clear();

        var windows = Windows();
        Visibility = windows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (windows.Count == 0) return;

        var screens = ZoneManager.ScreensLeftToRight();
        var byScreen = screens
            .Select((s, i) => (Screen: s, Number: i + 1, Windows: windows.Where(w => w.HMonitor == s.HMonitor).ToList()))
            .ToList();
        // Windows whose monitor we couldn't place go with the main monitor.
        var placed = byScreen.SelectMany(x => x.Windows).ToHashSet();
        var main = byScreen.FindIndex(x => x.Screen.Primary);
        if (main >= 0) byScreen[main].Windows.AddRange(windows.Where(w => !placed.Contains(w)));

        var shown = byScreen.Where(x => x.Windows.Count > 0).ToList();
        for (var i = 0; i < shown.Count; i++)
        {
            var (screen, number, list) = shown[i];
            _columns.ColumnDefinitions.Add(new ColumnDefinition());
            var column = new StackPanel { Margin = new Thickness(i == 0 ? 0 : 12, 0, 0, 0) };
            Grid.SetColumn(column, i);

            var label = screens.Count > 1 ? $"Monitor {number}{(screen.Primary ? " · main" : "")}" : "Open windows";
            column.Children.Add(Caption(label.ToUpperInvariant(), 11, dim: true, bold: true, margin: new Thickness(2, 0, 0, 8)));

            // App groups flow side by side and wrap, so wide screens fill up instead of leaving gaps.
            var groups = new WrapPanel();
            foreach (var group in list.GroupBy(AppKey).OrderBy(g => AppName(g.First()), StringComparer.OrdinalIgnoreCase))
                groups.Children.Add(AppGroup(group.ToList()));
            column.Children.Add(groups);

            if (i > 0)
            {
                var divider = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-7, 0, 0, 0) };
                divider.SetResourceReference(Border.BackgroundProperty, "EdgeBrush");
                Grid.SetColumn(divider, i);
                _columns.Children.Add(divider);
            }
            _columns.Children.Add(column);
        }
    }

    static string AppKey(ApplicationWindow w) =>
        !string.IsNullOrEmpty(w.AppUserModelID) ? w.AppUserModelID : w.WinFileName ?? w.Title ?? "";

    static readonly Dictionary<string, string?> ProductNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The name people know the app by: its Start menu name, else the program's product name
    /// ("Steam" rather than "Steam Client WebHelper"), else its description or file name.
    /// </summary>
    string AppName(ApplicationWindow w)
    {
        if (!string.IsNullOrEmpty(w.AppUserModelID) && _apps.Find(w.AppUserModelID) is { } app) return app.Name;
        if (!string.IsNullOrEmpty(w.WinFileName))
        {
            if (!ProductNames.TryGetValue(w.WinFileName, out var product))
            {
                try { product = System.Diagnostics.FileVersionInfo.GetVersionInfo(w.WinFileName).ProductName?.Trim(); }
                catch { product = null; }
                ProductNames[w.WinFileName] = product;
            }
            // Product names are usually short and recognisable; skip long or generic ones.
            if (!string.IsNullOrWhiteSpace(product) && product.Length <= 32 && !product.Contains("Operating System")) return Tidy(product);
        }
        if (!string.IsNullOrWhiteSpace(w.WinFileDescription) && w.WinFileDescription.Length <= 40) return Tidy(w.WinFileDescription);
        if (!string.IsNullOrEmpty(w.WinFileName)) return Tidy(Path.GetFileNameWithoutExtension(w.WinFileName));
        return w.Title ?? "App";
    }

    /// <summary>"Steam Client WebHelper" → "Steam", "PCAssistant.Desktop" → "PCAssistant".</summary>
    static string Tidy(string name)
    {
        foreach (var suffix in new[] { " Client WebHelper", " WebHelper", " Helper", ".Desktop", " Desktop App" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && name.Length > suffix.Length)
                return name[..^suffix.Length];
        return name;
    }

    UIElement AppGroup(List<ApplicationWindow> windows)
    {
        var first = windows[0];
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 6) };
        header.Children.Add(IconOf(first, 16));
        header.Children.Add(Caption(AppName(first), 12.5, bold: true, margin: new Thickness(8, 0, 0, 0)));
        if (windows.Count > 1) header.Children.Add(Caption($"  {windows.Count}", 12, dim: true));

        var cards = new WrapPanel();
        foreach (var w in windows) cards.Children.Add(Card(w));

        var group = new StackPanel { Margin = new Thickness(0, 0, 12, 10) };
        group.Children.Add(header);
        group.Children.Add(cards);
        return group;
    }

    UIElement Card(ApplicationWindow window)
    {
        var isLast = window.Handle == _lastActive;

        var shot = new Border { Height = ShotHeight, CornerRadius = new CornerRadius(4), ClipToBounds = true };
        shot.SetResourceReference(Border.BackgroundProperty, "TileBrush");
        _shotSlots[window.Handle] = shot;
        ShowShot(window, shot);

        var close = new Button
        {
            Style = (Style)FindResource("GhostButton"),
            Width = 24,
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 4, 0),
            ToolTip = "Close window",
            Visibility = Visibility.Hidden,
            Content = new TextBlock { Text = "", Style = (Style)FindResource("Icon"), FontSize = 10 }
        };
        close.SetResourceReference(BackgroundProperty, "MenuBrush");
        close.Click += (_, e) =>
        {
            e.Handled = true;
            window.Close();
        };

        var title = Caption(window.Title ?? "", 12, margin: new Thickness(2, 5, 2, 0));
        title.ToolTip = window.Title;

        var inner = new Grid();
        inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        inner.Children.Add(shot);
        inner.Children.Add(close);
        Grid.SetRow(title, 1);
        inner.Children.Add(title);

        var ring = new Border
        {
            Width = CardWidth,
            Padding = new Thickness(4),
            Margin = new Thickness(0, 0, 6, 6),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(isLast ? 2 : 1),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = inner
        };
        ring.SetResourceReference(Border.BorderBrushProperty, isLast ? "AccentBrush" : "EdgeBrush");

        ring.MouseEnter += (_, _) =>
        {
            close.Visibility = Visibility.Visible;
            ring.SetResourceReference(Border.BackgroundProperty, "TileHoverBrush");
        };
        ring.MouseLeave += (_, _) =>
        {
            close.Visibility = Visibility.Hidden;
            ring.Background = Brushes.Transparent;
        };
        ring.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInside(d, close)) return;
            TaskCommands.Activate.Execute(window);
        };
        ring.MouseUp += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle) window.Close();
        };

        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Pin to dock", () => DockPins.PinWindow(window)));
        menu.Items.Add(MenuItem("Minimise", () => window.Minimize()));
        menu.Items.Add(MenuItem("Close window", () => window.Close()));
        ring.ContextMenu = menu;
        return ring;
    }

    static bool IsInside(DependencyObject d, DependencyObject container)
    {
        for (var x = d; x != null; x = VisualTreeHelper.GetParent(x))
            if (x == container) return true;
        return false;
    }

    static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    void ShowShot(ApplicationWindow window, Border slot)
    {
        if (_shots.TryGetValue(window.Handle, out var picture) && picture != null)
        {
            slot.Child = new Image { Source = picture, Stretch = Stretch.UniformToFill, VerticalAlignment = VerticalAlignment.Top };
            return;
        }
        // No snapshot (yet, or minimised): the app's icon instead.
        var placeholder = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        placeholder.Children.Add(IconOf(window, 32));
        if (window.IsMinimized) placeholder.Children.Add(Caption("Minimised", 11, dim: true, margin: new Thickness(0, 4, 0, 0)));
        slot.Child = placeholder;
    }

    // ---------- snapshots ----------

    async Task CaptureAsync()
    {
        var run = ++_captureRun;
        // The window you were just using first, so its picture appears first.
        var handles = Windows().Where(w => !w.IsMinimized).Select(w => w.Handle)
            .OrderByDescending(h => h == _lastActive).ToList();
        foreach (var hwnd in handles)
        {
            if (run != _captureRun) return;
            var picture = await Task.Run(() => WindowSnapshots.Capture(hwnd, (int)(CardWidth * 2)));
            if (run != _captureRun) return;
            _shots[hwnd] = picture;
            if (picture != null && _shotSlots.TryGetValue(hwnd, out var slot) &&
                Windows().FirstOrDefault(w => w.Handle == hwnd) is { } window)
                ShowShot(window, slot);
        }
        // Forget snapshots of windows that have closed.
        foreach (var gone in _shots.Keys.Except(Windows().Select(w => w.Handle)).ToList()) _shots.Remove(gone);
    }

    /// <summary>The window's icon, bound so it appears once Windows has handed it over.</summary>
    static Image IconOf(ApplicationWindow window, double size)
    {
        var image = new Image { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        image.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(ApplicationWindow.Icon)) { Source = window });
        return image;
    }

    static TextBlock Caption(string text, double size, bool dim = false, bool bold = false, Thickness margin = default)
    {
        var t = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = margin
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, dim ? "TextDimBrush" : "TextBrush");
        return t;
    }
}
