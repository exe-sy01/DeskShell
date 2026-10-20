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
/// Every open window, beside the shell: Monitor 1's windows in a panel on the left, Monitor 2's on
/// the right (more monitors are split between the two sides), grouped by app, each window a preview
/// card. On narrower screens it all goes in one panel underneath the shell instead. Snapshots are
/// taken in the background when the shell opens, so opening stays instant.
/// </summary>
public sealed class WindowBoard
{
    double CardWidth = 150, ShotHeight = 84;

    readonly ShellManager _shell;
    readonly AppIndex _apps;
    readonly (Border Panel, ScrollViewer Scroll) _left, _right;
    readonly Dictionary<IntPtr, BitmapSource?> _shots = new();
    readonly Dictionary<IntPtr, Border> _shotSlots = new();
    readonly DispatcherTimer _rebuild;
    IntPtr _lastActive;
    int _captureRun;
    bool _open;
    bool _sides = true;

    /// <summary>Left-side panel (also the single panel when stacked under the shell).</summary>
    public Border Left => _left.Panel;
    /// <summary>Right-side panel (hidden when stacked).</summary>
    public Border Right => _right.Panel;

    public WindowBoard(ShellManager shell, AppIndex apps)
    {
        _shell = shell;
        _apps = apps;
        _left = MakePanel();
        _right = MakePanel();

        var dispatcher = Application.Current.Dispatcher;
        _rebuild = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) =>
        {
            _rebuild!.Stop();
            if (_open) Build();
        }, dispatcher);
        _rebuild.Stop();
        if (shell.Tasks.GroupedWindows?.SourceCollection is INotifyCollectionChanged changes)
            changes.CollectionChanged += (_, _) => dispatcher.InvokeAsync(() => { _rebuild.Stop(); _rebuild.Start(); });
    }

    static (Border, ScrollViewer) MakePanel()
    {
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 6, 0)
        };
        var panel = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 6, 6),
            Child = scroll
        };
        panel.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        panel.SetResourceReference(Border.BorderBrushProperty, "EdgeBrush");
        return (panel, scroll);
    }

    /// <summary>
    /// Side by side with the shell (<paramref name="sides"/>), each panel <paramref name="width"/> wide,
    /// or one panel under it. Lists scroll beyond <paramref name="maxHeight"/>.
    /// </summary>
    public void Arrange(bool sides, double width, double maxHeight)
    {
        _sides = sides;
        (CardWidth, ShotHeight) = sides ? (150.0, 84.0) : (168.0, 94.0);
        foreach (var (panel, scroll) in new[] { _left, _right })
        {
            panel.Width = width;
            scroll.MaxHeight = Math.Max(150, maxHeight - panel.Padding.Top - panel.Padding.Bottom - 2);
        }
        if (_open) Build();
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
        _shotSlots.Clear();
        var windows = Windows();
        var screens = ZoneManager.ScreensLeftToRight();
        var byScreen = screens
            .Select((s, i) => (Screen: s, Number: i + 1, Windows: windows.Where(w => w.HMonitor == s.HMonitor).ToList()))
            .ToList();
        // Windows whose monitor we couldn't place go with the main monitor.
        var placed = byScreen.SelectMany(x => x.Windows).ToHashSet();
        var main = byScreen.FindIndex(x => x.Screen.Primary);
        if (main >= 0) byScreen[main].Windows.AddRange(windows.Where(w => !placed.Contains(w)));

        if (_sides)
        {
            // Left monitors on the left, right monitors on the right; a single monitor goes on the right.
            var half = byScreen.Count / 2;
            Fill(_left, byScreen.Take(half).ToList(), screens.Count);
            Fill(_right, byScreen.Skip(half).ToList(), screens.Count);
        }
        else
        {
            Fill(_left, byScreen.Where(x => x.Windows.Count > 0).ToList(), screens.Count, columns: true);
            _right.Panel.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Puts monitor sections into a panel: stacked (beside the shell) or as columns (under it).</summary>
    void Fill((Border Panel, ScrollViewer Scroll) target, List<(AppBarScreen Screen, int Number, List<ApplicationWindow> Windows)> monitors,
        int screenCount, bool columns = false)
    {
        if (monitors.Count == 0 || monitors.All(m => m.Windows.Count == 0) && !_sides)
        {
            target.Panel.Visibility = _sides ? Visibility.Hidden : Visibility.Collapsed; // hidden keeps the shell centred
            return;
        }
        target.Panel.Visibility = Visibility.Visible;

        var container = new Grid();
        for (var i = 0; i < monitors.Count; i++)
        {
            var (screen, number, list) = monitors[i];
            var section = new StackPanel { Margin = columns ? new Thickness(i == 0 ? 0 : 12, 0, 0, 0) : new Thickness(0, i == 0 ? 0 : 8, 0, 0) };
            var label = screenCount > 1 ? $"Monitor {number}{(screen.Primary ? " · main" : "")}" : "Open windows";
            section.Children.Add(Caption(label.ToUpperInvariant(), 11, dim: true, bold: true, margin: new Thickness(2, 0, 0, 8)));

            if (list.Count == 0)
            {
                section.Children.Add(Caption("No windows", 12, dim: true, margin: new Thickness(2, 0, 0, 8)));
            }
            else
            {
                // App groups flow side by side and wrap.
                var groups = new WrapPanel();
                foreach (var group in list.GroupBy(AppKey).OrderBy(g => AppName(g.First()), StringComparer.OrdinalIgnoreCase))
                    groups.Children.Add(AppGroup(group.ToList()));
                section.Children.Add(groups);
            }

            if (columns)
            {
                container.ColumnDefinitions.Add(new ColumnDefinition());
                Grid.SetColumn(section, i);
            }
            else
            {
                container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(section, i);
            }
            container.Children.Add(section);
        }
        target.Scroll.Content = container;
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
            Style = (Style)Application.Current.FindResource("GhostButton"),
            Width = 24,
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 4, 0),
            ToolTip = "Close window",
            Visibility = Visibility.Hidden,
            Content = new TextBlock { Text = "", Style = (Style)Application.Current.FindResource("Icon"), FontSize = 10 }
        };
        close.SetResourceReference(Control.BackgroundProperty, "MenuBrush");
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
