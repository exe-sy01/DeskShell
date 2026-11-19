using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DeskShell.WindowManagement;

namespace DeskShell.Files;

/// <summary>
/// DeskShell's file explorer: tabs, a sidebar of pinned folders and drives, a details list or
/// thumbnail tiles, and Windows' own copy, move, delete and right-click menus.
/// </summary>
public partial class FilesWindow : Window
{
    public static readonly GridLength ModifiedWidth = new(160), TypeWidth = new(160), SizeWidth = new(96);
    const string Home = "";

    readonly ObservableCollection<FolderTab> _tabs = new();
    readonly ObservableCollection<SideItem> _side = new();
    readonly DispatcherTimer _refreshTimer, _deviceTimer;
    FolderTab _tab = null!;
    FileSystemWatcher? _watcher;
    IntPtr _hwnd;
    string? _selectAfterLoad, _renameAfterLoad;
    ScrollViewer? _listScroller;

    public FilesWindow()
    {
        ThemeManager.Use(Resources);
        InitializeComponent();
        var prefs = FileView.Prefs;
        Width = Math.Max(MinWidth, prefs.Width);
        Height = Math.Max(MinHeight, prefs.Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SidebarColumn.Width = new GridLength(Math.Clamp(prefs.SidebarWidth, 150, 420));
        TabStrip.ItemsSource = _tabs;
        Sidebar.ItemsSource = _side;

        _refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) =>
        {
            _refreshTimer!.Stop();
            _ = RefreshAsync(_tab);
        }, Dispatcher) { IsEnabled = false };
        _deviceTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            _deviceTimer!.Stop();
            BuildSidebar();
            if (_tab.Location == Home) ShowHome();
        }, Dispatcher) { IsEnabled = false };

        SourceInitialized += OnSourceInitialized;
        StateChanged += (_, _) => FitMaximized();
        SizeChanged += (_, _) => FitTabs();
        Closing += (_, _) => SavePlacement();
        Closed += (_, _) =>
        {
            _watcher?.Dispose();
            foreach (var tab in _tabs) tab.Loading?.Cancel();
            FileView.PinsChanged -= OnPinsChanged;
            ThemeManager.Release(Resources);
            foreach (var tab in _tabs)
            {
                tab.Items.Clear();
                tab.Shown.Clear();
            }
        };
        FileView.PinsChanged += OnPinsChanged;
        BuildSidebar();
        UpdateSortHeaders();
        if (prefs.Maximized) WindowState = WindowState.Maximized;
    }

    void OnPinsChanged()
    {
        BuildSidebar();
        if (_tab?.Location == Home) ShowHome();
    }

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var on = ThemeManager.IsDark ? 1 : 0;
        WmNative.DwmSetWindowAttribute(_hwnd, 20, ref on, sizeof(int)); // frame follows the theme
        var round = 2;
        WmNative.DwmSetWindowAttribute(_hwnd, 33, ref round, sizeof(int)); // rounded corners, Windows 11
        var source = HwndSource.FromHwnd(_hwnd);
        ShellMenu.Attach(source);
        source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == 0x219 /* WM_DEVICECHANGE */) { _deviceTimer.Stop(); _deviceTimer.Start(); }
            return IntPtr.Zero;
        });
        FitMaximized();
        RestorePanels();
    }

    void FitMaximized()
    {
        if (WindowState == WindowState.Maximized)
        {
            var t = SystemParameters.WindowResizeBorderThickness;
            Root.Margin = new Thickness(t.Left + 1, t.Top + 1, t.Right + 1, t.Bottom + 1);
            MaximizeGlyph.Text = "";
        }
        else
        {
            Root.Margin = new Thickness(0);
            MaximizeGlyph.Text = "";
        }
    }

    void SavePlacement()
    {
        var prefs = FileView.Prefs;
        prefs.Maximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            prefs.Width = ActualWidth;
            prefs.Height = ActualHeight;
        }
        FileView.Save();
    }

    // ---------- tabs ----------

    /// <summary>Opens a folder (or Home if null) in a new tab, selecting <paramref name="select"/> if given.</summary>
    public void OpenTab(string? location, string? select = null)
    {
        var tab = new FolderTab();
        _tabs.Add(tab);
        ActivateTab(tab, load: false);
        Navigate(location ?? Home, remember: false, select: select);
        FitTabs();
    }

    void ActivateTab(FolderTab tab, bool load = true)
    {
        if (_tab == tab) return;
        if (_tab != null)
        {
            RememberViewState();
            _tab.IsActive = false;
        }
        _tab = tab;
        tab.IsActive = true;
        if (!load) return;
        ShowLocation(restore: true);
        if (tab.Location != Home) _ = RefreshAsync(tab);
    }

    void CloseTab(FolderTab tab)
    {
        tab.Loading?.Cancel();
        var index = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        if (_tabs.Count == 0)
        {
            Close();
            return;
        }
        if (tab == _tab)
        {
            _tab = null!;
            ActivateTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
        }
        FitTabs();
    }

    void FitTabs()
    {
        if (_tabs.Count == 0) return;
        var available = Math.Max(200, ActualWidth - 46 * 3 - 70);
        var each = Math.Clamp(available / _tabs.Count, 100, 230);
        foreach (var tab in _tabs) tab.MaxWidth = each;
    }

    void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FolderTab tab) ActivateTab(tab);
    }

    void Tab_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && (sender as FrameworkElement)?.DataContext is FolderTab tab) CloseTab(tab);
    }

    void Tab_DragOver(object sender, DragEventArgs e)
    {
        // Hovering a tab while dragging files switches to it, so you can drop into its folder.
        if ((sender as FrameworkElement)?.DataContext is FolderTab tab && tab != _tab) ActivateTab(tab);
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FolderTab tab) CloseTab(tab);
    }

    void NewTab_Click(object sender, RoutedEventArgs e) => OpenTab(null);

    // ---------- navigation ----------

    /// <summary>Goes to a folder, a file's folder (selecting the file), or Home. Returns false if there's no such place.</summary>
    public bool Navigate(string target, bool remember = true, string? select = null)
    {
        var location = Resolve(target, out var file);
        if (location == null)
        {
            if (file != null) return true; // handed to Explorer or opened
            return false;
        }
        select ??= file;
        var tab = _tab;
        if (remember && !string.Equals(tab.Location, location, StringComparison.OrdinalIgnoreCase) && tab.HasLoaded)
        {
            RememberViewState();
            tab.Back.Push(tab.Location);
            tab.Forward.Clear();
        }
        tab.Location = location;
        tab.HasLoaded = true;
        _ = LoadAsync(tab, select);
        return true;
    }

    /// <summary>Turns typed or passed-in text into a folder path, Home (""), or null.</summary>
    string? Resolve(string input, out string? file)
    {
        file = null;
        var text = Environment.ExpandEnvironmentVariables(input.Trim().Trim('"'));
        if (text.Length == 0 || text.Equals("Home", StringComparison.OrdinalIgnoreCase) || text.Equals("This PC", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("20D04FE0-3AEA-1069-A2D8-08002B30309D", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("679f85cb-0220-4080-b29b-5540cc05aab6", StringComparison.OrdinalIgnoreCase) || // Quick access
            text.Contains("f874310e-b6b7-47dc-bc84-b9e6b38f5903", StringComparison.OrdinalIgnoreCase) || // Home
            text.Equals("shell:MyComputerFolder", StringComparison.OrdinalIgnoreCase))
            return Home;

        if (text.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("::"))
        {
            var folder = ShellNative.ShellPathToFolder(text);
            if (folder != null) return Normalize(folder);
            FilesHost.OpenInExplorer(text); // Recycle Bin, Control Panel and other places that aren't folders
            file = text;
            return null;
        }

        try
        {
            if (text.Length == 2 && text[1] == ':') text += "\\";
            if (!Path.IsPathRooted(text) && _tab != null && _tab.Location != Home) text = Path.GetFullPath(Path.Combine(_tab.Location, text));
            if (Directory.Exists(text)) return Normalize(text);
            if (File.Exists(text))
            {
                file = Path.GetFullPath(text);
                return Normalize(Path.GetDirectoryName(file)!);
            }
        }
        catch
        {
            // not a path
        }
        return null;
    }

    static string Normalize(string path)
    {
        path = Path.GetFullPath(path);
        var root = Path.GetPathRoot(path);
        if (root != null && string.Equals(path.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return root.EndsWith('\\') ? root : root + "\\";
        return path.TrimEnd('\\');
    }

    void Back_Click(object sender, RoutedEventArgs e) => GoBack();
    void Forward_Click(object sender, RoutedEventArgs e) => GoForward();
    void Up_Click(object sender, RoutedEventArgs e) => GoUp();
    void Refresh_Click(object sender, RoutedEventArgs e) => RefreshNow();

    void GoBack()
    {
        if (_tab.Back.Count == 0) return;
        RememberViewState();
        var came = _tab.Location;
        _tab.Forward.Push(came);
        _tab.Location = _tab.Back.Pop();
        _ = LoadAsync(_tab, ChildOf(_tab.Location, came));
    }

    void GoForward()
    {
        if (_tab.Forward.Count == 0) return;
        RememberViewState();
        _tab.Back.Push(_tab.Location);
        _tab.Location = _tab.Forward.Pop();
        _ = LoadAsync(_tab, null);
    }

    void GoUp()
    {
        if (_tab.Location == Home) return;
        var parent = Path.GetDirectoryName(_tab.Location.TrimEnd('\\'));
        var came = _tab.Location;
        Navigate(string.IsNullOrEmpty(parent) || _tab.Location.Length <= 3 ? Home : parent, select: came);
    }

    /// <summary>Going back up selects the folder you came from, like Explorer.</summary>
    static string? ChildOf(string parent, string came) =>
        parent != Home && came.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(parent, came[(parent.TrimEnd('\\').Length + 1)..].Split('\\')[0])
            : null;

    void RefreshNow()
    {
        if (_tab.Location == Home)
        {
            BuildSidebar();
            ShowHome();
        }
        else if (_tab.SearchQuery != null) StartDeepSearch(_tab.SearchQuery);
        else _ = LoadAsync(_tab, null, keepView: true);
    }

    FileComparer Comparer => new(FileView.Prefs.SortBy, FileView.Prefs.SortDescending);

    /// <summary>Loads a tab's folder; a failure is logged rather than lost in a fire-and-forget task.</summary>
    async Task LoadAsync(FolderTab tab, string? select, bool keepView = false)
    {
        try
        {
            await LoadCoreAsync(tab, select, keepView);
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't show {tab.Location}", ex);
            if (tab == _tab) Status.Text = "Couldn't show this folder: " + ex.Message;
        }
    }

    async Task LoadCoreAsync(FolderTab tab, string? select, bool keepView)
    {
        tab.Loading?.Cancel();
        var cancel = new CancellationTokenSource();
        tab.Loading = cancel;
        tab.SearchQuery = null;
        tab.Filter = "";
        var location = tab.Location;
        tab.Title = TitleFor(location);
        tab.Glyph = location == Home ? "" : FileView.GlyphFor(location);
        if (tab == _tab)
        {
            if (SearchBox.Text.Length > 0) SetSearchText("");
            ShowChrome();
        }
        if (location == Home)
        {
            tab.Items = new();
            tab.Shown = tab.Items;
            if (tab == _tab) ShowLocation(restore: false);
            return;
        }

        var slow = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            if (tab == _tab && tab.Loading == cancel) Status.Text = "Loading…";
        }, Dispatcher);
        List<FolderEntry> entries;
        string? error = null;
        try
        {
            entries = await Task.Run(() => ReadFolder(location, cancel.Token), cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            entries = new();
            error = ex is UnauthorizedAccessException ? "You don't have permission to open this folder." : ex.Message;
        }
        finally
        {
            slow.Stop();
        }
        if (cancel.IsCancellationRequested || tab.Location != location) return;

        var items = entries.Select(entry => new FileItem(location, entry)).ToList();
        items.Sort(Comparer);
        tab.Items = new ObservableCollection<FileItem>(items);
        tab.Shown = tab.Items;
        tab.Error = error;
        tab.Tiles = FileView.TilesFor(location);
        if (!keepView)
        {
            tab.ScrollOffset = 0;
            tab.Selection.Clear();
        }
        if (select != null) tab.Selection = new List<string> { select };
        if (tab == _tab)
        {
            ShowLocation(restore: true);
            MeasureFolders();
        }
    }

    /// <summary>Lists a folder; an empty result is checked, as an unreadable folder also reads as empty.</summary>
    static List<FolderEntry> ReadFolder(string location, CancellationToken cancel)
    {
        var entries = FolderReader.Read(location, FileView.ShowHidden, cancel);
        if (entries.Count == 0)
        {
            if (!Directory.Exists(location)) throw new DirectoryNotFoundException("This folder doesn't exist any more.");
            using var check = Directory.EnumerateFileSystemEntries(location).GetEnumerator();
            check.MoveNext(); // throws if access is denied
        }
        return entries;
    }

    /// <summary>Re-reads the folder and updates the list in place, keeping selection and scroll position.</summary>
    async Task RefreshAsync(FolderTab tab)
    {
        if (tab.Location == Home || !tab.HasLoaded) return;
        var location = tab.Location;
        List<FolderEntry> entries;
        try
        {
            entries = await Task.Run(() => ReadFolder(location, CancellationToken.None));
        }
        catch
        {
            if (tab.Location == location && !Directory.Exists(location) && tab == _tab) GoUp(); // it was deleted
            return;
        }
        if (tab.Location != location) return;

        var existing = new Dictionary<string, FileItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in tab.Items) existing.TryAdd(item.Name, item);
        var desired = new List<FileItem>(entries.Count);
        foreach (var entry in entries)
        {
            if (existing.TryGetValue(entry.Name, out var item) && item.IsFolder == entry.IsFolder && item.Name == entry.Name)
            {
                item.Update(entry);
                desired.Add(item);
            }
            else desired.Add(new FileItem(location, entry));
        }
        desired.Sort(Comparer);

        tab.Error = null;
        var changes = Math.Abs(desired.Count - tab.Items.Count) + desired.Count(d => !existing.ContainsKey(d.Name));
        if (changes > 400)
        {
            RememberViewState();
            tab.Items = new ObservableCollection<FileItem>(desired);
            if (tab.SearchQuery == null) tab.Shown = tab.Filter.Length == 0 ? tab.Items : new ObservableCollection<FileItem>(desired.Where(i => Matches(i, tab.Filter)));
            if (tab == _tab && tab.SearchQuery == null) ShowLocation(restore: true);
        }
        else
        {
            Sync(tab.Items, desired);
            if (tab.SearchQuery == null && tab.Filter.Length > 0) Sync(tab.Shown, desired.Where(i => Matches(i, tab.Filter)).ToList());
        }
        if (tab != _tab) return;
        UpdateEmptyNote();
        UpdateStatus();
        ApplyPendingSelection();
    }

    /// <summary>Makes <paramref name="target"/> match <paramref name="desired"/> with as few changes as possible, so selection survives.</summary>
    static void Sync(ObservableCollection<FileItem> target, List<FileItem> desired)
    {
        var want = new HashSet<FileItem>(desired);
        for (var i = target.Count - 1; i >= 0; i--)
            if (!want.Contains(target[i])) target.RemoveAt(i);
        var present = new HashSet<FileItem>(target);
        for (var j = 0; j < desired.Count; j++)
        {
            var item = desired[j];
            if (j < target.Count && ReferenceEquals(target[j], item)) continue;
            if (present.Contains(item)) target.Move(target.IndexOf(item), j);
            else target.Insert(j, item);
        }
    }

    void ApplyPendingSelection()
    {
        var path = _renameAfterLoad ?? _selectAfterLoad;
        if (path == null) return;
        var item = _tab.Shown.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
        if (item == null) return;
        var rename = _renameAfterLoad != null;
        _renameAfterLoad = _selectAfterLoad = null;
        SelectOnly(item);
        if (rename) StartRename(item);
    }

    void Watch(string location)
    {
        _watcher?.Dispose();
        _watcher = null;
        if (location == Home) return;
        try
        {
            var watcher = new FileSystemWatcher(location)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes,
                InternalBufferSize = 64 * 1024
            };
            void Changed() => Dispatcher.BeginInvoke(() =>
            {
                if (_watcher != watcher) return;
                _refreshTimer.Stop();
                _refreshTimer.Start();
            });
            watcher.Created += (_, _) => Changed();
            watcher.Deleted += (_, _) => Changed();
            watcher.Renamed += (_, _) => Changed();
            watcher.Changed += (_, _) => Changed();
            watcher.Error += (_, _) => Changed();
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch
        {
            // some drives can't be watched; F5 still works
        }
    }

    // ---------- showing the current place ----------

    void ShowChrome()
    {
        var location = _tab.Location;
        Title = _tab.Title;
        BackButton.IsEnabled = _tab.Back.Count > 0;
        ForwardButton.IsEnabled = _tab.Forward.Count > 0;
        UpButton.IsEnabled = location != Home;
        BuildCrumbs();
        // Highlight the sidebar entry for this place, if there is one.
        Sidebar.SelectedItem = _side.FirstOrDefault(s => !s.IsHeader && s.Path != null &&
                                                         string.Equals(s.Path.TrimEnd('\\'), location.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
    }

    void ShowLocation(bool restore)
    {
        ShowChrome();
        if (_tab.Location == Home)
        {
            _watcher?.Dispose();
            _watcher = null;
            FolderPage.Visibility = Visibility.Collapsed;
            HomePage.Visibility = Visibility.Visible;
            ShowHome();
            return;
        }
        HomePage.Visibility = Visibility.Collapsed;
        FolderPage.Visibility = Visibility.Visible;
        ApplyViewMode();
        List.ItemsSource = _tab.Shown;
        UpdateEmptyNote();
        if (_watcher?.Path != _tab.Location) Watch(_tab.Location);

        var selection = restore ? _tab.Selection.ToList() : new List<string>();
        var offset = restore ? _tab.ScrollOffset : 0;
        Dispatcher.BeginInvoke(() =>
        {
            var scroller = ListScroller();
            List.SelectedItems.Clear();
            FileItem? first = null;
            if (selection.Count > 0)
            {
                var wanted = new HashSet<string>(selection, StringComparer.OrdinalIgnoreCase);
                foreach (var item in _tab.Shown)
                {
                    if (!wanted.Contains(item.Path)) continue;
                    List.SelectedItems.Add(item);
                    first ??= item;
                }
            }
            if (offset > 0) scroller?.ScrollToVerticalOffset(offset);
            else if (first != null) List.ScrollIntoView(first);
            else scroller?.ScrollToTop();
            FocusList(first);
            UpdateStatus();
        }, DispatcherPriority.Loaded);
        UpdateStatus();
    }

    void RememberViewState()
    {
        if (_tab == null || _tab.Location == Home) return;
        _tab.Selection = List.SelectedItems.Cast<FileItem>().Select(i => i.Path).ToList();
        _tab.ScrollOffset = ListScroller()?.VerticalOffset ?? 0;
    }

    ScrollViewer? ListScroller()
    {
        if (_listScroller != null) return _listScroller;
        DependencyObject? node = List;
        while (node != null && node is not ScrollViewer)
            node = VisualTreeHelper.GetChildrenCount(node) > 0 ? VisualTreeHelper.GetChild(node, 0) : null;
        return _listScroller = node as ScrollViewer;
    }

    void FocusList(FileItem? item)
    {
        if (Keyboard.FocusedElement is TextBox box && (box == AddressBox || box == SearchBox)) return;
        if (item != null && List.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container) container.Focus();
        else if (List.Items.Count > 0 && List.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem firstContainer && List.SelectedItems.Count == 0)
            firstContainer.Focus();
        else List.Focus();
    }

    void SelectOnly(FileItem item)
    {
        List.SelectedItems.Clear();
        List.SelectedItem = item;
        List.ScrollIntoView(item);
        Dispatcher.BeginInvoke(() => FocusList(item), DispatcherPriority.Loaded);
    }

    void UpdateEmptyNote()
    {
        if (_tab.Error != null)
        {
            EmptyGlyph.Text = "";
            EmptyText.Text = _tab.Error;
            EmptyNote.Visibility = Visibility.Visible;
        }
        else if (_tab.Shown.Count == 0 && !_tab.Searching)
        {
            EmptyGlyph.Text = _tab.SearchQuery != null || _tab.Filter.Length > 0 ? "" : "";
            EmptyText.Text = _tab.SearchQuery != null ? "Nothing found here. Alt+Enter searches every drive."
                : _tab.Filter.Length > 0 ? "No matches in this folder. Enter searches its subfolders too, Alt+Enter every drive." : "This folder is empty.";
            EmptyNote.Visibility = Visibility.Visible;
        }
        else EmptyNote.Visibility = Visibility.Collapsed;
    }

    void UpdateStatus()
    {
        if (_tab.Location == Home)
        {
            Status.Text = "";
            return;
        }
        if (_tab.Error != null)
        {
            Status.Text = _tab.Error;
            return;
        }
        var count = _tab.Shown.Count;
        var text = _tab.SearchQuery != null
            ? (_tab.Searching ? $"Searching… {count:N0} found" : $"{count:N0} result{(count == 1 ? "" : "s")}") + $" for \"{_tab.SearchQuery}\""
            : $"{count:N0} item{(count == 1 ? "" : "s")}";
        var selected = List.SelectedItems.Cast<FileItem>().ToList();
        if (selected.Count > 0)
        {
            text += $"     {selected.Count:N0} selected";
            var files = selected.Where(i => !i.IsFolder).ToList();
            if (files.Count > 0) text += $"  ·  {FileItem.FormatSize(files.Sum(f => f.Size))}";
        }
        Status.Text = text;
        var any = selected.Count > 0;
        CutButton.IsEnabled = CopyButton.IsEnabled = DeleteButton.IsEnabled = any;
        RenameButton.IsEnabled = selected.Count == 1;
    }

    string TitleFor(string location)
    {
        if (location == Home) return "Home";
        var root = Path.GetPathRoot(location);
        if (root != null && string.Equals(root.TrimEnd('\\'), location.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return DriveName(location);
        return Path.GetFileName(location.TrimEnd('\\'));
    }

    /// <summary>"Local Disk (C:)" and the like.</summary>
    static string DriveName(string root)
    {
        var info = new ShellNative.SHFILEINFO();
        if (ShellNative.SHGetFileInfo(root, 0, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf<ShellNative.SHFILEINFO>(), 0x200 /* SHGFI_DISPLAYNAME */) != IntPtr.Zero
            && !string.IsNullOrEmpty(info.szDisplayName))
            return info.szDisplayName;
        return root.TrimEnd('\\');
    }

    // ---------- view mode and sorting ----------

    void ApplyViewMode()
    {
        var tiles = _tab.Tiles;
        List.ItemsPanel = (ItemsPanelTemplate)Resources[tiles ? "TilesPanel" : "DetailsPanel"];
        List.ItemTemplate = (DataTemplate)Resources[tiles ? "TileItem" : "DetailsRow"];
        List.ItemContainerStyle = (Style)Resources[tiles ? "TileBox" : "Row"];
        VirtualizingPanel.SetScrollUnit(List, tiles ? ScrollUnit.Item : ScrollUnit.Pixel);
        ColumnHeaders.Visibility = tiles ? Visibility.Collapsed : Visibility.Visible;
        TypeHeader.Content = HeaderText("Type", _tab.SearchQuery != null ? "Location" : "Type");
        DetailsToggle.Foreground = tiles ? (Brush)FindResource("TextDimBrush") : (Brush)FindResource("AccentBrush");
        TilesToggle.Foreground = tiles ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("TextDimBrush");
        _listScroller = null;
    }

    void SetView(bool tiles)
    {
        if (_tab.Location == Home || _tab.Tiles == tiles) return;
        RememberViewState();
        _tab.Tiles = tiles;
        if (_tab.SearchQuery == null) FileView.RememberView(_tab.Location, tiles);
        ApplyViewMode();
        var selection = _tab.Selection.ToList();
        Dispatcher.BeginInvoke(() =>
        {
            var item = _tab.Shown.FirstOrDefault(i => selection.Contains(i.Path));
            if (item != null) List.ScrollIntoView(item);
            FocusList(item);
        }, DispatcherPriority.Loaded);
    }

    void DetailsView_Click(object sender, RoutedEventArgs e) => SetView(false);
    void TilesView_Click(object sender, RoutedEventArgs e) => SetView(true);

    void Header_Click(object sender, RoutedEventArgs e)
    {
        var by = (string)((FrameworkElement)sender).Tag;
        var prefs = FileView.Prefs;
        if (prefs.SortBy == by) prefs.SortDescending = !prefs.SortDescending;
        else
        {
            prefs.SortBy = by;
            prefs.SortDescending = by is "Modified" or "Size";
        }
        ApplySort();
    }

    void ApplySort()
    {
        FileView.Save();
        UpdateSortHeaders();
        RememberViewState();
        var comparer = Comparer;
        foreach (var tab in _tabs)
        {
            tab.Items = new ObservableCollection<FileItem>(tab.Items.OrderBy(i => i, comparer));
            tab.Shown = tab.Filter.Length == 0 && tab.SearchQuery == null ? tab.Items : new ObservableCollection<FileItem>(tab.Shown.OrderBy(i => i, comparer));
        }
        if (_tab.Location != Home) ShowLocation(restore: true);
    }

    void UpdateSortHeaders()
    {
        NameHeader.Content = HeaderText("Name", "Name");
        ModifiedHeader.Content = HeaderText("Modified", "Date modified");
        TypeHeader.Content = HeaderText("Type", _tab?.SearchQuery != null ? "Location" : "Type");
        SizeHeader.Content = HeaderText("Size", "Size");
    }

    static object HeaderText(string key, string label)
    {
        var prefs = FileView.Prefs;
        if (prefs.SortBy != key) return label;
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(new TextBlock
        {
            Text = prefs.SortDescending ? "" : "",
            FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
            FontSize = 9,
            Margin = new Thickness(6, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        return panel;
    }

    // ---------- address bar ----------

    void BuildCrumbs()
    {
        Crumbs.Children.Clear();
        var location = _tab.Location;
        var parts = new List<(string Name, string Path)>();
        if (location == Home) parts.Add(("Home", Home));
        else
        {
            var root = Path.GetPathRoot(location) ?? location;
            parts.Add((root.StartsWith(@"\\") ? root.TrimEnd('\\') : DriveName(root), Normalize(root)));
            var rest = location[root.Length..].Trim('\\');
            var path = root.TrimEnd('\\');
            foreach (var part in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                path += "\\" + part;
                parts.Add((part, path));
            }
        }
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0)
                Crumbs.Children.Add(new TextBlock
                {
                    Text = "",
                    FontFamily = (FontFamily)FindResource("IconFont"),
                    FontSize = 9,
                    Foreground = (Brush)FindResource("TextDimBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 1, 2, 0)
                });
            var (name, target) = parts[i];
            var button = new Button
            {
                Style = (Style)FindResource("GhostButton"),
                Width = double.NaN,
                Height = 26,
                Tag = target,
                Cursor = Cursors.Hand,
                Focusable = false,
                AllowDrop = true,
                Content = new TextBlock { Text = name, Margin = new Thickness(7, 0, 7, 0), FontSize = 13 }
            };
            button.Click += (_, _) => Navigate((string)button.Tag);
            button.PreviewMouseUp += (_, e) =>
            {
                if (e.ChangedButton != MouseButton.Middle) return;
                OpenTab((string)button.Tag);
                e.Handled = true;
            };
            button.DragOver += (_, e) => DragOverFolder(e, (string)button.Tag);
            button.Drop += (_, e) => DropInto(e, (string)button.Tag);
            Crumbs.Children.Add(button);
        }
        Dispatcher.BeginInvoke(() => CrumbScroller.ScrollToRightEnd(), DispatcherPriority.Loaded);
    }

    void CrumbScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        CrumbScroller.ScrollToHorizontalOffset(CrumbScroller.HorizontalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    void AddressBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindParent<Button>(source) != null) return;
        EditAddress();
        e.Handled = true;
    }

    void EditAddress()
    {
        AddressBox.Text = _tab.Location == Home ? "" : _tab.Location;
        AddressBox.Visibility = Visibility.Visible;
        CrumbScroller.Visibility = Visibility.Hidden;
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    void EndAddressEdit()
    {
        AddressBox.Visibility = Visibility.Collapsed;
        CrumbScroller.Visibility = Visibility.Visible;
    }

    void AddressBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            EndAddressEdit();
            FocusList(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            var text = AddressBox.Text.Trim();
            e.Handled = true;
            if (Navigate(text))
            {
                EndAddressEdit();
                FocusList(null);
                return;
            }
            // Not a place: run it like Explorer's address bar does (cmd, notepad, a website…).
            if (text.Length > 0 && FileOps.Open(text))
            {
                EndAddressEdit();
                return;
            }
            Status.Text = $"Windows can't find \"{text}\".";
        }
    }

    void AddressBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => EndAddressEdit();

    // ---------- search ----------

    bool _settingSearch;

    void SetSearchText(string text)
    {
        _settingSearch = true;
        SearchBox.Text = text;
        _settingSearch = false;
    }

    static bool Matches(FileItem item, string filter) => item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_settingSearch || _tab.Location == Home) return;
        var filter = SearchBox.Text.Trim();
        if (_tab.SearchQuery != null)
        {
            // Typing again after a deep search goes back to filtering this folder.
            _tab.Loading?.Cancel();
            _tab.SearchQuery = null;
            _tab.Searching = false;
            ApplyViewMode();
        }
        _tab.Filter = filter;
        _tab.Shown = filter.Length == 0 ? _tab.Items : new ObservableCollection<FileItem>(_tab.Items.Where(i => Matches(i, filter)));
        List.ItemsSource = _tab.Shown;
        UpdateEmptyNote();
        UpdateStatus();
    }

    /// <summary>Search files (every drive, from the index), with what's typed in the search box.</summary>
    void SearchEverywhere()
    {
        var text = SearchBox.Text.Trim();
        FilesHost.App?.OpenSearch(text.Length > 0 ? text : null);
    }

    void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Alt+Enter, or Enter on Home (which has no folder to search): every drive.
        if ((e.Key == Key.System && e.SystemKey == Key.Enter || e.Key == Key.Enter && _tab.Location == Home) && SearchBox.Text.Trim().Length > 0)
        {
            SearchEverywhere();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && SearchBox.Text.Trim().Length > 0 && _tab.Location != Home)
        {
            StartDeepSearch(SearchBox.Text.Trim());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (SearchBox.Text.Length > 0) SearchBox.Text = "";
            FocusList(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            if (List.Items.Count > 0)
            {
                List.SelectedIndex = 0;
                FocusList((FileItem)List.Items[0]);
            }
            e.Handled = true;
        }
    }

    /// <summary>Searches this folder and everything under it, showing results as they're found.</summary>
    void StartDeepSearch(string query)
    {
        var tab = _tab;
        tab.Loading?.Cancel();
        var cancel = new CancellationTokenSource();
        tab.Loading = cancel;
        tab.SearchQuery = query;
        tab.Searching = true;
        tab.Filter = "";
        var results = new ObservableCollection<FileItem>();
        tab.Shown = results;
        List.ItemsSource = results;
        ApplyViewMode();
        UpdateSortHeaders();
        UpdateEmptyNote();
        UpdateStatus();
        var location = tab.Location;
        var showHidden = FileView.ShowHidden;
        Task.Run(() => FolderReader.Search(location, query, showHidden, batch =>
            Dispatcher.BeginInvoke(() =>
            {
                if (cancel.IsCancellationRequested) return;
                foreach (var item in batch) results.Add(item);
                if (tab == _tab) UpdateStatus();
            }, DispatcherPriority.Background), cancel.Token), cancel.Token)
            .ContinueWith(_ => Dispatcher.BeginInvoke(() =>
            {
                if (cancel.IsCancellationRequested) return;
                tab.Searching = false;
                if (tab != _tab) return;
                UpdateEmptyNote();
                UpdateStatus();
            }, DispatcherPriority.Background));
    }

    // ---------- the list: selection, opening, drag and drop ----------

    List<FileItem> Selected => List.SelectedItems.Cast<FileItem>().ToList();
    List<string> SelectedPaths => Selected.Select(i => i.Path).ToList();

    /// <summary>Where new files go: the folder on screen (not Home or search results).</summary>
    string? CurrentFolder => _tab.Location == Home ? null : _tab.Location;

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateStatus();
        SchedulePreview();
    }

    void OpenItems(IReadOnlyList<FileItem> items, bool newTab = false)
    {
        if (items.Count == 0) return;
        if (items.Count == 1 && items[0].IsFolder && !newTab)
        {
            Navigate(items[0].Path);
            return;
        }
        foreach (var item in items.Take(20))
        {
            if (item.IsFolder) OpenTab(item.Path);
            else OpenFile(item.Path, newTab);
        }
    }

    void OpenFile(string path, bool newTab = false)
    {
        // A shortcut to a folder opens here, like Explorer.
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && ShellNative.LinkTarget(path) is { } target && Directory.Exists(target))
        {
            if (newTab) OpenTab(target);
            else Navigate(target);
            return;
        }
        FileOps.Open(path);
    }

    static ListBoxItem? ContainerAt(object source) => source is DependencyObject d ? FindParent<ListBoxItem>(d) : null;

    static T? FindParent<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T found) return found;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    Point _dragStart;
    FileItem? _dragItem, _selectOnlyOnUp;
    bool _banding;
    Point _bandStart;
    HashSet<FileItem> _bandBase = new();

    void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && (FindParent<TextBox>(d) != null || FindParent<ScrollBar>(d) != null)) return;
        var container = ContainerAt(e.OriginalSource);
        if (container == null)
        {
            // Empty space: drag out a selection rectangle.
            if (Keyboard.Modifiers == ModifierKeys.None) List.SelectedItems.Clear();
            _bandBase = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? Selected.ToHashSet() : new();
            _bandStart = e.GetPosition(List);
            _banding = true;
            List.CaptureMouse();
            List.Focus();
            e.Handled = true;
            return;
        }
        var item = (FileItem)container.DataContext;
        if (e.ClickCount == 2)
        {
            OpenItems(new[] { item }, newTab: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
            e.Handled = true;
            return;
        }
        _dragStart = e.GetPosition(List);
        _dragItem = item;
        // Pressing on one of several selected items keeps them all selected, so they can be dragged together.
        if (container.IsSelected && Keyboard.Modifiers == ModifierKeys.None && List.SelectedItems.Count > 1)
        {
            _selectOnlyOnUp = item;
            container.Focus();
            e.Handled = true;
        }
    }

    void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_banding)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndBand();
                return;
            }
            UpdateBand(e.GetPosition(List));
            return;
        }
        if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        var position = e.GetPosition(List);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var items = Selected.Contains(_dragItem) ? Selected : new List<FileItem> { _dragItem };
        _dragItem = null;
        _selectOnlyOnUp = null;
        var data = new DataObject(DataFormats.FileDrop, items.Select(i => i.Path).ToArray());
        try { DragDrop.DoDragDrop(List, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link); }
        catch (Exception ex) { Log.Error("Drag failed", ex); }
    }

    void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_banding) EndBand();
        if (_selectOnlyOnUp != null) SelectOnly(_selectOnlyOnUp);
        _selectOnlyOnUp = null;
        _dragItem = null;
    }

    void UpdateBand(Point now)
    {
        var rect = new Rect(_bandStart, now);
        Canvas.SetLeft(Band, rect.X);
        Canvas.SetTop(Band, rect.Y);
        Band.Width = rect.Width;
        Band.Height = rect.Height;
        if (rect.Width < 3 && rect.Height < 3) return;
        Band.Visibility = Visibility.Visible;
        foreach (var container in RealizedContainers())
        {
            if (container.DataContext is not FileItem item) continue;
            var bounds = container.TransformToAncestor(List).TransformBounds(new Rect(container.RenderSize));
            container.IsSelected = bounds.IntersectsWith(rect) || _bandBase.Contains(item);
        }
    }

    IEnumerable<ListBoxItem> RealizedContainers()
    {
        for (var i = 0; i < List.Items.Count; i++)
        {
            // Only the containers that exist; ContainerFromIndex returns null for the rest.
            if (List.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem container && container.IsVisible) yield return container;
        }
    }

    void EndBand()
    {
        _banding = false;
        Band.Visibility = Visibility.Collapsed;
        List.ReleaseMouseCapture();
        UpdateStatus();
    }

    void List_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (ContainerAt(e.OriginalSource)?.DataContext is FileItem { IsFolder: true } item) OpenTab(item.Path);
    }

    // ---------- dropping files in ----------

    string? DropTarget(DragEventArgs e)
    {
        if (ContainerAt(e.OriginalSource)?.DataContext is FileItem { IsFolder: true } folder) return folder.Path;
        return _tab.SearchQuery == null ? CurrentFolder : null;
    }

    void List_DragOver(object sender, DragEventArgs e) => DragOverFolder(e, DropTarget(e));
    void List_Drop(object sender, DragEventArgs e) => DropInto(e, DropTarget(e));
    void List_DragLeave(object sender, DragEventArgs e) { }

    void DragOverFolder(DragEventArgs e, string? target)
    {
        e.Effects = EffectFor(e, target);
        e.Handled = true;
    }

    static DragDropEffects EffectFor(DragEventArgs e, string? target)
    {
        if (target == null || target == Home || e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return DragDropEffects.None;
        var into = target.TrimEnd('\\');
        if (files.Any(f => string.Equals(f.TrimEnd('\\'), into, StringComparison.OrdinalIgnoreCase))) return DragDropEffects.None;
        var sameFolder = files.All(f => string.Equals(Path.GetDirectoryName(f.TrimEnd('\\')), into, StringComparison.OrdinalIgnoreCase));
        DragDropEffects wanted;
        if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0) wanted = DragDropEffects.Copy;
        else if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0) wanted = DragDropEffects.Move;
        else
        {
            if (sameFolder) return DragDropEffects.None;
            var sameDrive = files.All(f => string.Equals(Path.GetPathRoot(f), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase));
            wanted = sameDrive ? DragDropEffects.Move : DragDropEffects.Copy;
        }
        if (sameFolder && wanted == DragDropEffects.Move) return DragDropEffects.None;
        return (e.AllowedEffects & wanted) != 0 ? wanted : (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
    }

    void DropInto(DragEventArgs e, string? target)
    {
        e.Handled = true;
        var effect = EffectFor(e, target);
        if (effect == DragDropEffects.None || target == null || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        if (effect == DragDropEffects.Move) FileOps.Move(_hwnd, files, target);
        else FileOps.Copy(_hwnd, files, target);
        e.Effects = effect;
    }

    // ---------- renaming ----------

    void StartRename(FileItem item)
    {
        foreach (var other in _tab.Shown.Where(i => i.IsRenaming)) other.IsRenaming = false;
        List.SelectedItems.Clear();
        List.SelectedItem = item;
        List.ScrollIntoView(item);
        item.IsRenaming = true;
    }

    void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox box || !(bool)e.NewValue || box.DataContext is not FileItem { IsRenaming: true } item) return;
        box.Text = item.Name;
        Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            Keyboard.Focus(box);
            var dot = item.IsFolder ? -1 : item.Name.LastIndexOf('.');
            box.Select(0, dot > 0 ? dot : item.Name.Length);
        }, DispatcherPriority.Input);
    }

    void RenameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape)
        {
            FinishRename((TextBox)sender, save: e.Key == Key.Enter);
            e.Handled = true;
        }
    }

    void RenameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => FinishRename((TextBox)sender, save: true);

    void FinishRename(TextBox box, bool save)
    {
        if (box.DataContext is not FileItem { IsRenaming: true } item) return;
        item.IsRenaming = false;
        var name = box.Text.Trim();
        if (save && name.Length > 0 && name != item.Name)
        {
            // Changing a file's extension can make it unusable; Explorer asks, so do we.
            if (!item.IsFolder && !string.Equals(Path.GetExtension(name), item.Extension, StringComparison.OrdinalIgnoreCase) &&
                MessageBox.Show(this, "If you change a file name extension, the file might become unusable.\n\nAre you sure you want to change it?", "Rename",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                FocusList(item);
                return;
            }
            var error = FileOps.Rename(item.Path, name);
            if (error != null)
            {
                MessageBox.Show(this, error, "Rename", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                _selectAfterLoad = Path.Combine(item.Folder, name);
                _ = RefreshAsync(_tab);
            }
        }
        FocusList(item);
    }

    // ---------- commands ----------

    void CutOrCopy(bool cut)
    {
        var items = Selected;
        if (items.Count == 0) return;
        FileOps.ToClipboard(items.Select(i => i.Path).ToList(), cut);
        foreach (var tab in _tabs)
        foreach (var item in tab.Items.Where(i => i.IsCut))
            item.IsCut = false;
        if (cut) foreach (var item in items) item.IsCut = true;
    }

    void Paste(string? into = null)
    {
        var target = into ?? CurrentFolder;
        if (target == null) return;
        FileOps.Paste(_hwnd, target);
    }

    void DeleteSelected(bool permanently)
    {
        var paths = SelectedPaths;
        if (paths.Count == 0) return;
        // Select a neighbour so the keyboard keeps working after the items disappear.
        var last = List.Items.IndexOf(List.SelectedItems[^1]);
        var next = Enumerable.Range(last + 1, Math.Max(0, List.Items.Count - last - 1)).Select(i => (FileItem)List.Items[i]).FirstOrDefault(i => !paths.Contains(i.Path))
                   ?? _tab.Shown.LastOrDefault(i => !paths.Contains(i.Path));
        _selectAfterLoad = next?.Path;
        FileOps.Delete(_hwnd, paths, permanently);
    }

    void NewFolder()
    {
        var folder = CurrentFolder;
        if (folder == null) return;
        try
        {
            _renameAfterLoad = FileOps.NewFolder(folder);
            _ = RefreshAsync(_tab);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "New folder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void NewTextFile()
    {
        var folder = CurrentFolder;
        if (folder == null) return;
        try
        {
            _renameAfterLoad = FileOps.NewFile(folder, "New Text Document", ".txt");
            _ = RefreshAsync(_tab);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "New file", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void ShowProperties(IReadOnlyList<FileItem> items)
    {
        if (items.Count == 0)
        {
            if (CurrentFolder is { } folder) FileOps.Properties(folder);
            return;
        }
        if (items.Count == 1) FileOps.Properties(items[0].Path);
        else ShellMenu.InvokeVerb(_hwnd, items[0].Folder, items.Where(i => i.Folder == items[0].Folder).Select(i => i.Path).ToList(), "properties");
    }

    void CopyPaths(IEnumerable<string> paths)
    {
        try { Clipboard.SetText(string.Join(Environment.NewLine, paths.Select(p => $"\"{p}\""))); }
        catch (Exception ex) { Log.Error("Couldn't copy paths", ex); }
    }

    void Cut_Click(object sender, RoutedEventArgs e) => CutOrCopy(true);
    void Copy_Click(object sender, RoutedEventArgs e) => CutOrCopy(false);
    void Paste_Click(object sender, RoutedEventArgs e) => Paste();
    void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));

    void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is [var item]) StartRename(item);
    }

    // ---------- menus ----------

    static MenuItem Item(string header, Action action, string? gesture = null, bool enabled = true, bool bold = false)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled };
        if (bold) item.FontWeight = FontWeights.SemiBold;
        item.Click += (_, _) => action();
        return item;
    }

    static MenuItem Check(string header, bool on, Action action)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = on };
        item.Click += (_, _) => action();
        return item;
    }

    void ShowMenu(FrameworkElement anchor, ContextMenu menu)
    {
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    void NewMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Folder", NewFolder, "Ctrl+Shift+N", CurrentFolder != null));
        menu.Items.Add(Item("Text document", NewTextFile, enabled: CurrentFolder != null));
        ShowMenu((FrameworkElement)sender, menu);
    }

    void SortMenu_Click(object sender, RoutedEventArgs e) => ShowMenu((FrameworkElement)sender, SortMenu());

    ContextMenu SortMenu()
    {
        var menu = new ContextMenu();
        AddSortItems(menu.Items);
        return menu;
    }

    void AddSortItems(ItemCollection items)
    {
        var prefs = FileView.Prefs;
        foreach (var (key, label) in new[] { ("Name", "Name"), ("Modified", "Date modified"), ("Type", "Type"), ("Size", "Size") })
            items.Add(Check(label, prefs.SortBy == key, () =>
            {
                prefs.SortBy = key;
                prefs.SortDescending = key is "Modified" or "Size";
                ApplySort();
            }));
        items.Add(new Separator());
        items.Add(Check("Ascending", !prefs.SortDescending, () => { prefs.SortDescending = false; ApplySort(); }));
        items.Add(Check("Descending", prefs.SortDescending, () => { prefs.SortDescending = true; ApplySort(); }));
    }

    void ViewMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        AddViewItems(menu.Items);
        ShowMenu((FrameworkElement)sender, menu);
    }

    void AddViewItems(ItemCollection items)
    {
        items.Add(Check("Details", !_tab.Tiles, () => SetView(false)));
        items.Add(Check("Tiles", _tab.Tiles, () => SetView(true)));
        items.Add(new Separator());
        items.Add(Check("Hidden items", FileView.ShowHidden, () =>
        {
            FileView.Prefs.ShowHidden = !FileView.ShowHidden;
            FileView.Save();
            RefreshNow();
        }));
        items.Add(Check("File name extensions", FileView.ShowExtensions, () =>
        {
            FileView.Prefs.ShowExtensions = !FileView.ShowExtensions;
            FileView.Save();
            foreach (var tab in _tabs)
            foreach (var item in tab.Items)
                item.RefreshName();
        }));
    }

    void MoreMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var folder = CurrentFolder;
        menu.Items.Add(Item("Open in Terminal", () => FileOps.TerminalHere(folder!), enabled: folder != null));
        menu.Items.Add(Item("Open in Windows File Explorer", () => FilesHost.OpenInExplorer(folder ?? "shell:MyComputerFolder")));
        menu.Items.Add(Item("Copy path", () => CopyPaths(new[] { folder! }), enabled: folder != null));
        if (folder != null)
        {
            var pinned = FileView.IsPinned(folder);
            menu.Items.Add(Item(pinned ? "Unpin from sidebar" : "Pin to sidebar", () => FileView.SetPinned(folder, !pinned)));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Select all", () => List.SelectAll(), "Ctrl+A", folder != null || _tab.SearchQuery != null));
        menu.Items.Add(Item("Invert selection", InvertSelection, enabled: folder != null));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("New window", () => FilesHost.App?.OpenWindow(_tab.Location), "Ctrl+N"));
        menu.Items.Add(Item("Properties", () => ShowProperties(Array.Empty<FileItem>()), enabled: folder != null));
        ShowMenu((FrameworkElement)sender, menu);
    }

    void InvertSelection()
    {
        var selected = Selected.ToHashSet();
        List.SelectedItems.Clear();
        foreach (var item in _tab.Shown.Where(i => !selected.Contains(i))) List.SelectedItems.Add(item);
    }

    void List_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var fromMouse = e.CursorLeft >= 0;
        var container = fromMouse ? ContainerAt(e.OriginalSource) : null;
        if (fromMouse && container == null) List.SelectedItems.Clear();
        else if (container != null && !container.IsSelected) SelectOnly((FileItem)container.DataContext);
        var items = Selected;

        // One menu: Windows' own (with 7-Zip, Open with, Send to…) and DeskShell's commands at the top.
        // Shift+right-click adds Windows' extended commands, like Explorer.
        e.Handled = true;
        ShowShellMenu(items, extended: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    /// <summary>DeskShell's own commands for the selection, shown above Windows' in the one right-click menu.</summary>
    List<ShellMenu.Extra> ItemExtras(List<FileItem> items)
    {
        var extras = new List<ShellMenu.Extra>();
        var one = items.Count == 1 ? items[0] : null;
        var folders = items.All(i => i.IsFolder);
        if (folders)
        {
            extras.Add(new("Open in new tab", () => { foreach (var f in items) OpenTab(f.Path); }));
            extras.Add(new("Open in new window", () => { foreach (var f in items.Take(5)) FilesHost.App?.OpenWindow(f.Path); }));
        }
        if (one is { IsFolder: false }) extras.Add(new("Quick Look\tSpace", () => QuickLook(one)));
        if (_tab.SearchQuery != null && one != null) extras.Add(new("Open file location", () => Navigate(one.Folder, select: one.Path)));
        extras.Add(new("Copy as path\tCtrl+Shift+C", () => CopyPaths(items.Select(i => i.Path))));
        extras.Add(ShellMenu.Extra.Separator);
        extras.Add(new("Compress to ZIP", () => CompressItems(items)));
        if (one != null && Archive.IsZip(one.Path)) extras.Add(new("Extract here", () => ExtractZip(one.Path)));
        if (items.Count > 1) extras.Add(new("Rename all…", () => BatchRename(items)));
        if (one is { IsFolder: true })
        {
            extras.Add(ShellMenu.Extra.Separator);
            var pinned = FileView.IsPinned(one.Path);
            extras.Add(new(pinned ? "Unpin from sidebar" : "Pin to sidebar", () => FileView.SetPinned(one.Path, !pinned)));
            extras.Add(new("Open in Terminal", () => FileOps.TerminalHere(one.Path)));
            if (_dual != null) extras.Add(new("Open in the other pane", () => _ = OtherPaneOpen(one.Path)));
        }
        return extras;
    }

    /// <summary>DeskShell's commands for the folder's background: view, sort, new, terminal, pin.</summary>
    List<ShellMenu.Extra> BackgroundExtras(string folder)
    {
        var prefs = FileView.Prefs;
        var view = new List<ShellMenu.Extra>
        {
            new("Details", () => SetView(false), Checked: !_tab.Tiles),
            new("Tiles", () => SetView(true), Checked: _tab.Tiles),
            ShellMenu.Extra.Separator,
            new("Hidden items", () => { FileView.Prefs.ShowHidden = !FileView.ShowHidden; FileView.Save(); RefreshNow(); }, Checked: FileView.ShowHidden),
            new("File name extensions", ToggleExtensions, Checked: FileView.ShowExtensions),
            ShellMenu.Extra.Separator,
            new("Preview pane", () => SetSidePanel(_sidePanel == "preview" ? null : "preview"), Checked: _sidePanel == "preview"),
            new("Details pane", () => SetSidePanel(_sidePanel == "details" ? null : "details"), Checked: _sidePanel == "details"),
            new("Two panes", ToggleDual, Checked: _dual != null),
            new("Folder sizes", ToggleFolderSizes, Checked: prefs.FolderSizes)
        };
        var sort = new List<ShellMenu.Extra>();
        foreach (var (key, label) in new[] { ("Name", "Name"), ("Modified", "Date modified"), ("Type", "Type"), ("Size", "Size") })
            sort.Add(new(label, () => { prefs.SortBy = key; prefs.SortDescending = key is "Modified" or "Size"; ApplySort(); }, Checked: prefs.SortBy == key));
        sort.Add(ShellMenu.Extra.Separator);
        sort.Add(new("Ascending", () => { prefs.SortDescending = false; ApplySort(); }, Checked: !prefs.SortDescending));
        sort.Add(new("Descending", () => { prefs.SortDescending = true; ApplySort(); }, Checked: prefs.SortDescending));
        var pinned = FileView.IsPinned(folder);
        return
        [
            new("View", Children: view),
            new("Sort by", Children: sort),
            new("Refresh\tF5", RefreshNow),
            ShellMenu.Extra.Separator,
            new("New folder\tCtrl+Shift+N", NewFolder),
            new("New text document", NewTextFile),
            ShellMenu.Extra.Separator,
            new("Open in Terminal", () => FileOps.TerminalHere(folder)),
            new(pinned ? "Unpin from sidebar" : "Pin to sidebar", () => FileView.SetPinned(folder, !pinned))
        ];
    }

    void ToggleExtensions()
    {
        FileView.Prefs.ShowExtensions = !FileView.ShowExtensions;
        FileView.Save();
        foreach (var tab in _tabs)
        foreach (var item in tab.Items)
            item.RefreshName();
    }

    void FillItemMenu(ContextMenu menu, List<FileItem> items)
    {
        var one = items.Count == 1 ? items[0] : null;
        var folders = items.All(i => i.IsFolder);
        menu.Items.Add(Item("Open", () => OpenItems(items), "Enter", bold: true));
        if (folders)
        {
            menu.Items.Add(Item("Open in new tab", () => { foreach (var f in items) OpenTab(f.Path); }));
            menu.Items.Add(Item("Open in new window", () => { foreach (var f in items.Take(5)) FilesHost.App?.OpenWindow(f.Path); }));
        }
        if (one is { IsFolder: false })
        {
            menu.Items.Add(Item("Open with…", () => ShellMenu.InvokeVerb(_hwnd, one.Folder, new[] { one.Path }, "openas")));
            if (one.Extension.ToLowerInvariant() is ".exe" or ".bat" or ".cmd" or ".msi" or ".ps1" or ".lnk")
                menu.Items.Add(Item("Run as administrator", () => FileOps.Open(one.Path, admin: true)));
        }
        if (_tab.SearchQuery != null && one != null)
            menu.Items.Add(Item("Open file location", () => Navigate(one.Folder, select: one.Path)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Cut", () => CutOrCopy(true), "Ctrl+X"));
        menu.Items.Add(Item("Copy", () => CutOrCopy(false), "Ctrl+C"));
        if (one is { IsFolder: true })
            menu.Items.Add(Item("Paste into folder", () => Paste(one.Path), enabled: FileOps.FromClipboard().Files.Count > 0));
        menu.Items.Add(Item("Copy as path", () => CopyPaths(items.Select(i => i.Path)), "Ctrl+Shift+C"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Rename", () => StartRename(one!), "F2", one != null));
        menu.Items.Add(Item("Delete", () => DeleteSelected(false), "Del"));
        if (one is { IsFolder: true })
        {
            menu.Items.Add(new Separator());
            var pinned = FileView.IsPinned(one.Path);
            menu.Items.Add(Item(pinned ? "Unpin from sidebar" : "Pin to sidebar", () => FileView.SetPinned(one.Path, !pinned)));
            menu.Items.Add(Item("Open in Terminal", () => FileOps.TerminalHere(one.Path)));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Properties", () => ShowProperties(items), "Alt+Enter"));
        menu.Items.Add(Item("Show more options", () => ShowShellMenu(items, extended: false), "Shift+F10"));
    }

    void FillBackgroundMenu(ContextMenu menu)
    {
        var folder = CurrentFolder;
        if (folder == null) return;
        var view = new MenuItem { Header = "View" };
        AddViewItems(view.Items);
        menu.Items.Add(view);
        var sort = new MenuItem { Header = "Sort by" };
        AddSortItems(sort.Items);
        menu.Items.Add(sort);
        menu.Items.Add(Item("Refresh", RefreshNow, "F5"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Paste", () => Paste(), "Ctrl+V", FileOps.FromClipboard().Files.Count > 0));
        var add = new MenuItem { Header = "New" };
        add.Items.Add(Item("Folder", NewFolder, "Ctrl+Shift+N"));
        add.Items.Add(Item("Text document", NewTextFile));
        menu.Items.Add(add);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Open in Terminal", () => FileOps.TerminalHere(folder)));
        var pinned = FileView.IsPinned(folder);
        menu.Items.Add(Item(pinned ? "Unpin from sidebar" : "Pin to sidebar", () => FileView.SetPinned(folder, !pinned)));
        menu.Items.Add(Item("Properties", () => ShowProperties(Array.Empty<FileItem>())));
        menu.Items.Add(Item("Show more options", () => ShowShellMenu(new List<FileItem>(), extended: false), "Shift+F10"));
    }

    /// <summary>Windows' own menu, at the mouse (or the selection when opened from the keyboard).</summary>
    void ShowShellMenu(List<FileItem> items, bool extended)
    {
        var folder = items.Count > 0 ? items[0].Folder : CurrentFolder;
        if (folder == null) return;
        var paths = items.Where(i => i.Folder == folder).Select(i => i.Path).ToList();
        // From the keyboard, open it by the selected item rather than wherever the mouse is.
        var point = PointToScreen(Mouse.GetPosition(this));
        var mouseInside = new Rect(0, 0, ActualWidth, ActualHeight).Contains(Mouse.GetPosition(this));
        if (!mouseInside || Keyboard.FocusedElement is ListBoxItem && !List.IsMouseOver)
        {
            var anchor = items.Count > 0 && List.ItemContainerGenerator.ContainerFromItem(items[^1]) is ListBoxItem container ? (FrameworkElement)container : List;
            point = anchor.PointToScreen(new Point(Math.Min(40, anchor.ActualWidth), anchor == List ? 10 : anchor.ActualHeight));
        }
        ShellMenu.Show(_hwnd, folder, paths, point, extended, verb =>
        {
            switch (verb.ToLowerInvariant())
            {
                case "rename" when items.Count == 1:
                    StartRename(items[0]);
                    return true;
                case "open" or "explore" when items.Count == 1 && items[0].IsFolder:
                    Navigate(items[0].Path);
                    return true;
                case "opennewtab" when items.All(i => i.IsFolder):
                    foreach (var f in items) OpenTab(f.Path);
                    return true;
                case "opennewwindow" when items.Count == 1 && items[0].IsFolder:
                    FilesHost.App?.OpenWindow(items[0].Path);
                    return true;
                case "pintohome" when items.Count == 1 && items[0].IsFolder:
                    FileView.SetPinned(items[0].Path, true);
                    return true;
                default:
                    return false;
            }
        }, items.Count > 0 ? ItemExtras(items) : BackgroundExtras(folder));
    }

    // ---------- keyboard and mouse ----------

    void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1) { GoBack(); e.Handled = true; }
        else if (e.ChangedButton == MouseButton.XButton2) { GoForward(); e.Handled = true; }
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        var ctrl = mods == ModifierKeys.Control;
        var ctrlShift = mods == (ModifierKeys.Control | ModifierKeys.Shift);
        var alt = mods == ModifierKeys.Alt;
        var inText = Keyboard.FocusedElement is TextBoxBase;
        bool Handle(Action action)
        {
            action();
            e.Handled = true;
            return true;
        }

        // Anywhere in the window
        if (alt && key == Key.Left || key == Key.BrowserBack) { Handle(GoBack); return; }
        if (alt && key == Key.Right || key == Key.BrowserForward) { Handle(GoForward); return; }
        if (alt && key == Key.Up) { Handle(GoUp); return; }
        if (alt && key == Key.D || ctrl && key == Key.L || key == Key.F4 && mods == ModifierKeys.None) { Handle(EditAddress); return; }
        if (ctrlShift && key == Key.F) { Handle(SearchEverywhere); return; }
        if (ctrl && key == Key.F || key == Key.F3 && mods == ModifierKeys.None || ctrl && key == Key.E) { Handle(() => { SearchBox.Focus(); SearchBox.SelectAll(); }); return; }
        if (ctrl && key == Key.T) { Handle(() => OpenTab(_tab.Location)); return; }
        if (ctrl && key == Key.W) { Handle(() => CloseTab(_tab)); return; }
        if (ctrl && key == Key.N) { Handle(() => FilesHost.App?.OpenWindow(_tab.Location)); return; }
        if (ctrl && key == Key.Tab || ctrl && key == Key.PageDown) { Handle(() => ActivateTab(_tabs[(_tabs.IndexOf(_tab) + 1) % _tabs.Count])); return; }
        if (ctrlShift && key == Key.Tab || ctrl && key == Key.PageUp) { Handle(() => ActivateTab(_tabs[(_tabs.IndexOf(_tab) + _tabs.Count - 1) % _tabs.Count])); return; }
        if (ctrl && key >= Key.D1 && key <= Key.D9)
        {
            var index = key == Key.D9 ? _tabs.Count - 1 : key - Key.D1;
            if (index < _tabs.Count) Handle(() => ActivateTab(_tabs[index]));
            return;
        }
        if (alt && key == Key.P) { Handle(() => SetSidePanel(_sidePanel == "preview" ? null : "preview")); return; }
        if (mods == (ModifierKeys.Alt | ModifierKeys.Shift) && key == Key.P) { Handle(() => SetSidePanel(_sidePanel == "details" ? null : "details")); return; }
        if (ctrl && key == Key.Oem5) { Handle(ToggleDual); return; }
        // With two panes, F5 copies and F6 moves the selection to the other pane (like file commanders).
        if (_dual != null && List.IsKeyboardFocusWithin && mods == ModifierKeys.None && key is Key.F5 or Key.F6) { Handle(() => TransferToOther(Selected.Select(i => i.Path).ToList(), move: key == Key.F6)); return; }
        if (key == Key.F5 || ctrl && key == Key.R) { Handle(RefreshNow); return; }
        if (ctrlShift && key == Key.N) { Handle(NewFolder); return; }
        if (ctrlShift && key == Key.D6) { Handle(() => SetView(false)); return; }
        if (ctrlShift && key == Key.D2) { Handle(() => SetView(true)); return; }
        if (inText) return;
        if (key == Key.Back && mods == ModifierKeys.None) { Handle(GoBack); return; }
        if (!List.IsKeyboardFocusWithin) return;

        // In the list
        var items = Selected;
        if (key == Key.Enter && mods == ModifierKeys.None) { Handle(() => OpenItems(items)); return; }
        if (key == Key.Enter && ctrl) { Handle(() => OpenItems(items, newTab: true)); return; }
        if (key == Key.Enter && alt) { Handle(() => ShowProperties(items)); return; }
        if (key == Key.Delete && mods == ModifierKeys.None) { Handle(() => DeleteSelected(false)); return; }
        if (key == Key.Delete && mods == ModifierKeys.Shift) { Handle(() => DeleteSelected(true)); return; }
        if (key == Key.F2 && items.Count == 1) { Handle(() => StartRename(items[0])); return; }
        if (ctrl && key == Key.C) { Handle(() => CutOrCopy(false)); return; }
        if (ctrl && key == Key.X) { Handle(() => CutOrCopy(true)); return; }
        if (ctrl && key == Key.V) { Handle(() => Paste()); return; }
        if (ctrlShift && key == Key.C) { Handle(() => CopyPaths(items.Select(i => i.Path))); return; }
        if (ctrl && key == Key.A) { Handle(() => List.SelectAll()); return; }
        if (key == Key.Space && mods == ModifierKeys.None && items.Count == 1) { Handle(() => QuickLook(items[0])); return; }
        if (key == Key.Escape && mods == ModifierKeys.None)
        {
            if (_tab.SearchQuery != null || _tab.Filter.Length > 0) Handle(() => SearchBox.Text = "");
            return;
        }
        // In tiles, Up and Down move a whole row.
        if (_tab.Tiles && key is Key.Up or Key.Down or Key.Left or Key.Right && mods is ModifierKeys.None or ModifierKeys.Shift && List.Items.Count > 0)
        {
            var panel = FindChild<TilePanel>(List);
            var columns = panel?.Columns ?? 1;
            var current = List.SelectedItems.Count > 0 ? List.Items.IndexOf(Keyboard.FocusedElement is ListBoxItem { DataContext: FileItem f } ? f : List.SelectedItems[^1]) : -1;
            var step = key switch { Key.Down => columns, Key.Up => -columns, Key.Right => 1, _ => -1 };
            var next = Math.Clamp(current + step, 0, List.Items.Count - 1);
            if (current < 0) next = 0;
            var target = (FileItem)List.Items[next];
            if (mods == ModifierKeys.Shift) List.SelectedItems.Add(target);
            else SelectOnly(target);
            panel?.ScrollToIndex(next);
            Dispatcher.BeginInvoke(() => FocusList(target), DispatcherPriority.Loaded);
            e.Handled = true;
        }
    }

    static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    // ---------- window buttons ----------

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void Splitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        FileView.Prefs.SidebarWidth = SidebarColumn.ActualWidth;
        FileView.Save();
    }

    // ---------- sidebar ----------

    void BuildSidebar()
    {
        var selectedPath = (Sidebar.SelectedItem as SideItem)?.Path;
        _side.Clear();
        _side.Add(new SideItem { Name = "Home", Glyph = "", Path = Home });
        _side.Add(SideItem.Header("Pinned"));
        foreach (var pin in FileView.Prefs.Pins)
            _side.Add(new SideItem { Name = PinName(pin), Glyph = FileView.GlyphFor(pin), Path = pin, IsPin = true, Detail = pin });
        _side.Add(SideItem.Header("Drives"));
        var drivesHeader = _side.Count;
        _side.Add(new SideItem { Name = "Recycle Bin", Glyph = "", Path = null, Special = "shell:RecycleBinFolder" });
        _ = FillDrivesAsync(drivesHeader);
        if (selectedPath != null) ShowChromeSelection();
    }

    void ShowChromeSelection()
    {
        if (_tab == null) return;
        Sidebar.SelectedItem = _side.FirstOrDefault(s => !s.IsHeader && s.Path != null &&
                                                         string.Equals(s.Path.TrimEnd('\\'), _tab.Location.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
    }

    static string PinName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\'));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    async Task FillDrivesAsync(int at)
    {
        var drives = await Task.Run(SideItem.Drives);
        // The sidebar may have been rebuilt meanwhile.
        if (at > _side.Count || _side.ElementAtOrDefault(at - 1)?.Name != "Drives") return;
        foreach (var drive in drives) _side.Insert(at++, drive);
        ShowChromeSelection();
    }

    SideItem? SideItemAt(object source) => source is DependencyObject d ? FindParent<ListBoxItem>(d)?.DataContext as SideItem : null;

    void Sidebar_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (SideItemAt(e.OriginalSource) is { IsHeader: false } item) OpenSideItem(item, newTab: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
    }

    void Sidebar_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && SideItemAt(e.OriginalSource) is { IsHeader: false, Path: not null } item) OpenTab(item.Path);
    }

    void Sidebar_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Sidebar.SelectedItem is SideItem { IsHeader: false } item)
        {
            OpenSideItem(item, false);
            e.Handled = true;
        }
    }

    void OpenSideItem(SideItem item, bool newTab)
    {
        if (item.Special != null)
        {
            FilesHost.OpenInExplorer(item.Special);
            ShowChromeSelection();
            return;
        }
        if (item.Path == null) return;
        if (newTab) OpenTab(item.Path);
        else if (!Navigate(item.Path)) Status.Text = $"{item.Name} isn't available.";
    }

    void Sidebar_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var item = SideItemAt(e.OriginalSource);
        var menu = Sidebar.ContextMenu!;
        menu.Items.Clear();
        if (item is not { IsHeader: false, Path: not null } || item.Path == Home)
        {
            e.Handled = true;
            return;
        }
        menu.Items.Add(Item("Open in new tab", () => OpenTab(item.Path)));
        menu.Items.Add(Item("Open in new window", () => FilesHost.App?.OpenWindow(item.Path)));
        if (item.IsPin)
        {
            var index = FileView.Prefs.Pins.FindIndex(p => string.Equals(p, item.Path, StringComparison.OrdinalIgnoreCase));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Move up", () => FileView.MovePin(item.Path, -1), enabled: index > 0));
            menu.Items.Add(Item("Move down", () => FileView.MovePin(item.Path, 1), enabled: index < FileView.Prefs.Pins.Count - 1));
            menu.Items.Add(Item("Unpin from sidebar", () => FileView.SetPinned(item.Path, false)));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Open in Terminal", () => FileOps.TerminalHere(item.Path)));
        menu.Items.Add(Item("Properties", () => FileOps.Properties(item.Path)));
    }

    void Sidebar_DragOver(object sender, DragEventArgs e) => DragOverFolder(e, SideItemAt(e.OriginalSource)?.Path);
    void Sidebar_Drop(object sender, DragEventArgs e) => DropInto(e, SideItemAt(e.OriginalSource)?.Path);

    // ---------- home ----------

    void ShowHome()
    {
        HomePins.ItemsSource = FileView.Prefs.Pins.Select(p => new SideItem { Name = PinName(p), Glyph = FileView.GlyphFor(p), Path = p, Detail = p, IsPin = true }).ToList();
        _ = FillHomeDrivesAsync();
        var recent = RecentItem.Load();
        HomeRecent.ItemsSource = recent;
        RecentHeading.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateStatus();
        Dispatcher.BeginInvoke(() => HomePage.Focus(), DispatcherPriority.Loaded);
    }

    async Task FillHomeDrivesAsync()
    {
        var drives = await Task.Run(SideItem.Drives);
        if (_tab.Location == Home) HomeDrives.ItemsSource = drives;
    }

    void HomeTile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SideItem { Path: not null } item)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) OpenTab(item.Path);
            else Navigate(item.Path);
        }
    }

    void HomeTile_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && (sender as FrameworkElement)?.DataContext is SideItem { Path: not null } item) OpenTab(item.Path);
    }

    void Recent_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RecentItem item) OpenFile(item.Path);
    }
}

/// <summary>One open tab: where it is, its history, and what's listed.</summary>
public sealed class FolderTab : INotifyPropertyChanged
{
    string _location = "", _title = "Home", _glyph = "";
    bool _isActive;
    double _maxWidth = 230;

    public string Location { get => _location; set => Set(ref _location, value); }
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Glyph { get => _glyph; set => Set(ref _glyph, value); }
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }
    public double MaxWidth { get => _maxWidth; set => Set(ref _maxWidth, value); }

    public bool HasLoaded;
    public readonly Stack<string> Back = new(), Forward = new();
    /// <summary>Everything in the folder, sorted.</summary>
    public ObservableCollection<FileItem> Items = new();
    /// <summary>What the list shows: Items, the ones matching the filter, or search results.</summary>
    public ObservableCollection<FileItem> Shown = new();
    public string Filter = "";
    public string? SearchQuery;
    public bool Searching;
    public string? Error;
    public bool Tiles;
    public CancellationTokenSource? Loading;
    public List<string> Selection = new();
    public double ScrollOffset;

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>A sidebar or Home entry: a pinned folder, a drive, or a section heading.</summary>
public sealed class SideItem
{
    public string Name { get; init; } = "";
    public string Glyph { get; init; } = "";
    public string? Path { get; init; }
    public string? Special { get; init; }
    public string? Detail { get; init; }
    public bool IsHeader { get; init; }
    public bool IsPin { get; init; }
    /// <summary>How full a drive is, 0–1, or -1 for no bar.</summary>
    public double Usage { get; init; } = -1;

    public Visibility HeaderVisibility => IsHeader ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RowVisibility => IsHeader ? Visibility.Collapsed : Visibility.Visible;
    public Visibility UsageVisibility => Usage >= 0 ? Visibility.Visible : Visibility.Collapsed;
    public GridLength UsedStar => new(Math.Max(0.001, Usage), GridUnitType.Star);
    public GridLength FreeStar => new(Math.Max(0.001, 1 - Usage), GridUnitType.Star);
    public Brush UsageBrush => Usage > 0.9 ? Brushes.IndianRed : (Brush)Application.Current.FindResource("AccentBrush");
    public Brush GlyphBrush => (Brush)Application.Current.FindResource(IsPin || Usage >= 0 ? "AccentBrush" : "TextBrush");

    public static SideItem Header(string name) => new() { Name = name, IsHeader = true };

    /// <summary>Ready drives with their space. Run off the UI thread: a sleeping network drive can take a while.</summary>
    public static List<SideItem> Drives()
    {
        var result = new List<SideItem>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.CDRom && !drive.IsReady) continue;
                if (!drive.IsReady) continue;
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? drive.DriveType switch { DriveType.Removable => "USB drive", DriveType.Network => "Network drive", DriveType.CDRom => "Disc", _ => "Local Disk" }
                    : drive.VolumeLabel;
                var used = drive.TotalSize > 0 ? 1 - (double)drive.TotalFreeSpace / drive.TotalSize : -1;
                result.Add(new SideItem
                {
                    Name = $"{label} ({drive.Name.TrimEnd('\\')})",
                    Glyph = drive.DriveType switch { DriveType.Removable => "", DriveType.Network => "", DriveType.CDRom => "", _ => "" },
                    Path = drive.RootDirectory.FullName,
                    Usage = used,
                    Detail = $"{FileItem.FormatSize(drive.TotalFreeSpace)} free of {FileItem.FormatSize(drive.TotalSize)}"
                });
            }
            catch
            {
                // a drive that went away
            }
        }
        return result;
    }
}

/// <summary>A recently opened file, from Windows' Recent items.</summary>
public sealed class RecentItem : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string When { get; init; } = "";

    ImageSource? _icon;
    bool _requested;
    public ImageSource? Icon
    {
        get
        {
            if (_icon == null && !_requested)
            {
                _requested = true;
                _icon = IconLoader.Small(Path, false, null, image =>
                {
                    _icon = image;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
                });
            }
            return _icon;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static List<RecentItem> Load()
    {
        try
        {
            var folder = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            return new DirectoryInfo(folder).EnumerateFiles("*.lnk")
                .OrderByDescending(f => f.LastWriteTime)
                .Where(f => System.IO.Path.GetExtension(System.IO.Path.GetFileNameWithoutExtension(f.Name)).Length > 0) // files, not folders
                .Take(16)
                .Select(f => new RecentItem { Name = System.IO.Path.GetFileNameWithoutExtension(f.Name), Path = f.FullName, When = f.LastWriteTime.ToString("g") })
                .ToList();
        }
        catch
        {
            return new();
        }
    }
}
