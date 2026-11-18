using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskShell.Services;

namespace DeskShell.Tasks;

/// <summary>
/// The programs that start when you sign in, each with its publisher, where it's started from, and
/// a switch. The switch works the way Windows' own Task Manager does (Explorer's StartupApproved
/// list, through <see cref="StartupApps"/>), so the two always agree. Entries for all users live in
/// HKEY_LOCAL_MACHINE and change through Windows' administrator prompt; the list is read again
/// afterwards so it shows what really happened.
/// </summary>
public sealed class StartupPage : Grid
{
    /// <summary>One startup program and what we found out about its file.</summary>
    sealed class Entry
    {
        public required StartupApp App { get; set; }
        public required string Publisher { get; init; }
        public required string Source { get; init; }
        /// <summary>The file to show in its folder: the program, or the shortcut in the Startup folder.</summary>
        public string? Location { get; init; }
        public ImageSource? Icon { get; init; }
    }

    readonly TextBox _search;
    readonly TextBlock _count;
    readonly StackPanel _list;
    readonly TextBlock _empty;
    List<Entry> _entries = new();
    int _generation;
    bool _loaded;

    /// <summary>"12 startup apps, 9 on", for the window's status line.</summary>
    public string Summary { get; private set; } = "";
    public event EventHandler? SummaryChanged;

    public StartupPage()
    {
        TaskPageParts.Base(this);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ---------- toolbar ----------
        _search = TaskPageParts.Search("Search startup apps");
        _search.TextChanged += (_, _) => Show();
        _count = TaskPageParts.Text("", "TextDimBrush", 12.5);
        _count.VerticalAlignment = VerticalAlignment.Center;
        _count.Margin = new Thickness(14, 0, 0, 0);
        var refresh = TaskPageParts.Ghost("", "Look again");
        refresh.Click += (_, _) => Refresh();
        var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition());
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_count, 1);
        Grid.SetColumn(refresh, 2);
        toolbar.Children.Add(_search);
        toolbar.Children.Add(_count);
        toolbar.Children.Add(refresh);

        // ---------- column names ----------
        var header = new Grid { Margin = new Thickness(13, 0, 15, 6) };
        Columns(header);
        (string Text, int Column)[] labels = [("Name", 1), ("Publisher", 2), ("Starts from", 3), ("Enabled", 5)];
        foreach (var (text, column) in labels)
        {
            var label = TaskPageParts.Text(text, "TextDimBrush", 12);
            if (column == 5) label.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(label, column);
            header.Children.Add(label);
        }

        // ---------- list ----------
        _list = new StackPanel();
        _empty = TaskPageParts.Text("Looking…", "TextDimBrush", 13);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.Margin = new Thickness(0, 40, 0, 0);
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new Grid { Children = { _list, _empty } },
            Focusable = false
        };

        Grid.SetRow(header, 1);
        Grid.SetRow(scroll, 2);
        Children.Add(toolbar);
        Children.Add(header);
        Children.Add(scroll);

        // Fills itself the first time it's shown; after that the window decides when to look again.
        Loaded += (_, _) =>
        {
            if (!_loaded) Refresh();
        };
    }

    /// <summary>Reads the startup programs again (on a background thread).</summary>
    public void Refresh() => _ = LoadAsync();

    /// <summary>Puts the cursor in the search box (for Ctrl+F).</summary>
    public void FocusSearch()
    {
        _search.Focus();
        _search.SelectAll();
    }

    async Task LoadAsync()
    {
        _loaded = true;
        var generation = ++_generation;
        List<Entry> entries;
        try
        {
            entries = await Task.Run(() => StartupApps.Read().Select(Describe).ToList());
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't list startup apps", ex);
            entries = new();
        }
        if (generation != _generation) return; // a newer read is on its way
        _entries = entries;
        Show();
    }

    /// <summary>Publisher and icon of a startup program (background thread). Shortcuts are followed to their program.</summary>
    static Entry Describe(StartupApp app)
    {
        var target = app.Path;
        if (target != null && target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            target = DeskShell.Files.ShellNative.LinkTarget(target) ?? target;

        string? company = null;
        try
        {
            if (target != null && File.Exists(target))
            {
                var company0 = FileVersionInfo.GetVersionInfo(target).CompanyName;
                company = string.IsNullOrWhiteSpace(company0) ? null : company0.Trim();
            }
        }
        catch
        {
            // unreadable file
        }

        var source = app.ApprovedKey == "StartupFolder"
            ? app.ForAllUsers ? "Startup folder (all users)" : "Startup folder"
            : app.ForAllUsers ? "Registry (all users)" : "Registry (you)";
        return new Entry
        {
            App = app,
            Publisher = company ?? "",
            Source = source,
            Location = app.Path,
            Icon = TaskPageParts.FileIcon(app.Path ?? target)
        };
    }

    void Show()
    {
        var search = _search.Text.Trim();
        bool Has(string? s) => s != null && s.Contains(search, StringComparison.CurrentCultureIgnoreCase);
        var shown = _entries.Where(e => search.Length == 0 || Has(e.App.Name) || Has(e.Publisher) || Has(e.App.Command) || Has(e.Source)).ToList();

        _list.Children.Clear();
        foreach (var entry in shown) _list.Children.Add(Row(entry));
        _empty.Text = _entries.Count == 0 ? "Nothing starts with Windows." : "No startup apps match.";
        _empty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSummary();
    }

    /// <summary>Icon, name, publisher, source, open-location and switch, in the same columns as the header.</summary>
    static void Columns(Grid grid)
    {
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
    }

    FrameworkElement Row(Entry entry)
    {
        var grid = new Grid();
        Columns(grid);

        var icon = new Image { Source = entry.Icon, Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);

        var name = TaskPageParts.Text(entry.App.Name, "TextBrush", 13.5, FontWeights.SemiBold);
        var command = TaskPageParts.Text(entry.App.Command, "FaintBrush", 11.5);
        command.ToolTip = entry.App.Command;
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), Children = { name, command } };
        Grid.SetColumn(names, 1);

        var publisher = TaskPageParts.Text(entry.Publisher.Length > 0 ? entry.Publisher : "Unknown publisher",
            entry.Publisher.Length > 0 ? "TextDimBrush" : "FaintBrush", 12.5);
        publisher.VerticalAlignment = VerticalAlignment.Center;
        publisher.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(publisher, 2);

        var source = TaskPageParts.Text(entry.Source, "TextDimBrush", 12.5);
        source.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(source, 3);

        var open = TaskPageParts.Ghost("", entry.Location != null ? "Open file location" : "The program's file wasn't found");
        TaskPageParts.Enable(open, entry.Location != null);
        open.Click += (_, _) =>
        {
            if (entry.Location != null) App.Instance.OpenFolder(Path.GetDirectoryName(entry.Location), select: entry.Location);
        };
        Grid.SetColumn(open, 4);

        var toggle = new CheckBox
        {
            IsChecked = entry.App.Enabled,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = entry.App.ForAllUsers ? "Starts for everyone: Windows asks for administrator permission to change it" : "Start when you sign in"
        };
        toggle.SetResourceReference(StyleProperty, "Switch");
        toggle.Click += async (_, _) => await ToggleAsync(entry, toggle);
        Grid.SetColumn(toggle, 5);

        grid.Children.Add(icon);
        grid.Children.Add(names);
        grid.Children.Add(publisher);
        grid.Children.Add(source);
        grid.Children.Add(open);
        grid.Children.Add(toggle);

        var card = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 9, 14, 9), Margin = new Thickness(0, 0, 0, 6), Child = grid };
        card.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        card.MouseEnter += (_, _) => card.SetResourceReference(Border.BackgroundProperty, "RaisedBrush");
        card.MouseLeave += (_, _) => card.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        return card;
    }

    /// <summary>
    /// The switch has already flipped. Your own entries change at once; all-users entries go through
    /// the administrator prompt (an elevated reg.exe) and the list is read again, so a "No" at the
    /// prompt simply puts the switch back.
    /// </summary>
    async Task ToggleAsync(Entry entry, CheckBox toggle)
    {
        var want = toggle.IsChecked == true;
        toggle.IsEnabled = false;
        try
        {
            var ok = await StartupApps.SetEnabled(entry.App, want);
            if (!ok)
            {
                toggle.IsChecked = !want;
                return;
            }
            if (entry.App.ForAllUsers)
            {
                await LoadAsync();
                return;
            }
            entry.App = entry.App with { Enabled = want };
            UpdateSummary();
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't change startup app {entry.App.Name}", ex);
            toggle.IsChecked = !want;
        }
        finally
        {
            toggle.IsEnabled = true;
        }
    }

    void UpdateSummary()
    {
        var on = _entries.Count(e => e.App.Enabled);
        Summary = $"{_entries.Count} startup apps, {on} on";
        _count.Text = Summary;
        SummaryChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// Small pieces both Task Manager pages build their UI from, so they look like the rest of DeskShell:
/// theme colours and styles are always referenced (not copied), so a theme change shows at once.
/// </summary>
internal static class TaskPageParts
{
    /// <summary>The app's font and text colour for everything inside the page.</summary>
    public static void Base(FrameworkElement page)
    {
        page.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        page.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
    }

    public static TextBlock Text(string text, string brush = "TextBrush", double size = 13, FontWeight? weight = null)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return block;
    }

    /// <summary>The themed search box; Escape empties it.</summary>
    public static TextBox Search(string placeholder)
    {
        var box = new TextBox { Tag = placeholder, Width = 280, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        box.SetResourceReference(FrameworkElement.StyleProperty, "SearchBox");
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || box.Text.Length == 0) return;
            box.Text = "";
            e.Handled = true;
        };
        return box;
    }

    static TextBlock Glyph(string glyph, double size)
    {
        var icon = new TextBlock { Text = glyph, FontSize = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        return icon;
    }

    /// <summary>A quiet square icon button.</summary>
    public static Button Ghost(string glyph, string tip)
    {
        var button = new Button { Content = Glyph(glyph, 14), ToolTip = tip, Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center };
        button.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
        return button;
    }

    /// <summary>A labelled button; the glyph goes in Tag, which the PillButton style shows as its icon.</summary>
    public static Button Pill(string glyph, string text, string? tip = null)
    {
        var button = new Button { Content = text, Tag = glyph, ToolTip = tip, VerticalAlignment = VerticalAlignment.Center };
        button.SetResourceReference(FrameworkElement.StyleProperty, "PillButton");
        return button;
    }

    /// <summary>The PillButton and ChipButton styles don't dim when disabled, so this does.</summary>
    public static void Enable(UIElement element, bool enabled)
    {
        element.IsEnabled = enabled;
        element.Opacity = enabled ? 1 : 0.4;
    }

    /// <summary>A dark rounded card, like the rest of the app.</summary>
    public static Border Card(UIElement child, double radius = 16)
    {
        var card = new Border { CornerRadius = new CornerRadius(radius), Child = child };
        card.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        return card;
    }

    /// <summary>Moves a list to the new order without replacing items, so selection and scrolling stay put.</summary>
    public static void Sync<T>(ObservableCollection<T> items, List<T> desired) where T : class
    {
        var wanted = desired.ToHashSet();
        for (var i = items.Count - 1; i >= 0; i--)
            if (!wanted.Contains(items[i])) items.RemoveAt(i);
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < items.Count && ReferenceEquals(items[i], desired[i])) continue;
            var at = items.IndexOf(desired[i]);
            if (at >= 0) items.Move(at, i);
            else items.Insert(i, desired[i]);
        }
    }

    /// <summary>A file's small icon, frozen so it can be made on a background thread.</summary>
    public static ImageSource? FileIcon(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var info = new SHFILEINFO();
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x100 /* SHGFI_ICON */) == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
}
