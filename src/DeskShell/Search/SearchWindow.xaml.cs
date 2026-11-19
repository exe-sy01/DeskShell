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
using DeskShell.Files;

namespace DeskShell.Search;

/// <summary>
/// "Search files": every file and folder on every drive, found as you type, in the island's look.
/// Lives in the file explorer's process next to the index. Enter opens, Ctrl+Enter shows it in its
/// folder, right-click gives the explorer's menu, and results can be dragged out.
/// </summary>
public partial class SearchWindow : Window
{
    /// <summary>Sorting by anything but the name reads every result's folder, size or date; above this many it's not offered.</summary>
    const int SortLimit = 250_000;

    readonly ResultList _list;
    readonly DispatcherTimer _refresh, _status;
    CancellationTokenSource? _cancel;
    SearchResult? _last;
    int _generation;
    string _sortBy = "Name";
    bool _descending, _hold, _hiding, _resorted;
    IntPtr _hwnd;
    QuickLookWindow? _quickLook;
    Point _dragStart;
    SearchRow? _dragRow;

    public SearchWindow()
    {
        InitializeComponent();
        _list = new ResultList(new SearchResult(new SearchQuery("", SearchKind.All), new()));
        List.ItemsSource = _list;
        List.IsTextSearchEnabled = false;

        // The index changed (files came and went, or a drive finished): search again, now and then, while open.
        _refresh = new DispatcherTimer(TimeSpan.FromMilliseconds(1200), DispatcherPriority.Background, (_, _) =>
        {
            _refresh!.Stop();
            if (IsVisible && !_hiding) RunSearch(refresh: true);
        }, Dispatcher);
        _refresh.Stop();
        FileIndex.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible && !_refresh.IsEnabled) _refresh.Start();
        });
        _status = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateStatus(), Dispatcher);
        _status.Stop();

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(_hwnd);
            ShellMenu.Attach(source); // "Send to", "Open with"… fill in through the window's messages
        };
        Deactivated += (_, _) =>
        {
            // Out of the way as soon as you click elsewhere, unless a menu, drag or Quick Look of ours has the focus.
            if (_hold || _quickLook is { IsVisible: true } || !IsVisible) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (!IsActive && !_hold && _quickLook is not { IsActive: true }) HideWindow();
            }, DispatcherPriority.Background);
        };
        SizeChanged += (_, _) =>
        {
            if (!IsVisible || WindowState != WindowState.Normal) return;
            FileView.Prefs.SearchWidth = Width;
            FileView.Prefs.SearchHeight = Height;
        };
    }

    // ---------- showing and hiding ----------

    /// <summary>Opens on the screen the mouse is on, with <paramref name="query"/> typed in (or the last search, selected).</summary>
    public void ShowFor(string? query)
    {
        var first = !IsVisible;
        if (first) Place();
        _hiding = false;
        Show();
        WindowManagement.WmNative.Activate(new WindowInteropHelper(this).EnsureHandle());
        Activate();
        if (query != null) SearchBox.Text = query;
        else if (first && SearchBox.Text.Length > 0) RunSearch(refresh: false);
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        SearchBox.CaretIndex = SearchBox.Text.Length;
        if (query == null) SearchBox.SelectAll();
        UpdateStatus();
        _status.Start();
        if (first)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(170);
            Panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
            PanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, 1, duration) { EasingFunction = ease });
            PanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, duration) { EasingFunction = ease });
        }
    }

    public void Toggle(string? query)
    {
        if (IsVisible && IsActive && query == null) HideWindow();
        else ShowFor(query);
    }

    /// <summary>Hides (the window is kept for next time) and lets go of the results.</summary>
    public void HideWindow()
    {
        if (!IsVisible || _hiding) return;
        _hiding = true;
        _cancel?.Cancel();
        _refresh.Stop();
        _status.Stop();
        _quickLook?.HideLook();
        FileView.Save();
        Hide();
        _hiding = false;
        _last = null;
        _list.Replace(new SearchResult(new SearchQuery("", SearchKind.All), new()), null, false, null);
        Help.Visibility = Visibility.Visible;
        FilesHost.App?.TrimSoon();
    }

    /// <summary>Centred near the top of the screen the mouse is on, at the size it had last.</summary>
    void Place()
    {
        var width = FileView.Prefs.SearchWidth is > 400 and var w ? w : 1040;
        var height = FileView.Prefs.SearchHeight is > 300 and var h ? h : 660;
        GetCursorPos(out var cursor);
        var monitor = MonitorFromPoint(cursor, 2 /* nearest */);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            Width = width;
            Height = height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }
        var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        var work = info.rcWork;
        var pxWidth = (int)Math.Min(width * scale, (work.Right - work.Left) * 0.96);
        var pxHeight = (int)Math.Min(height * scale, (work.Bottom - work.Top) * 0.9);
        var x = work.Left + (work.Right - work.Left - pxWidth) / 2;
        var y = work.Top + (int)((work.Bottom - work.Top) * 0.07);
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        SetWindowPos(hwnd, IntPtr.Zero, x, y, pxWidth, pxHeight, 0x0004 /* NOZORDER */ | 0x0010 /* NOACTIVATE */);
    }

    void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is TextBox or RadioButton) return;
        _hold = true;
        try { DragMove(); }
        catch (InvalidOperationException) { }
        finally { _hold = false; }
    }

    // ---------- searching ----------

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RunSearch(refresh: false);

    void Kind_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) RunSearch(refresh: false);
    }

    SearchKind Kind => KindFiles.IsChecked == true ? SearchKind.Files : KindFolders.IsChecked == true ? SearchKind.Folders : SearchKind.All;

    /// <summary>
    /// Searches on a background thread; a newer keystroke cancels an older search. Typing more
    /// letters only filters the previous results. <paramref name="refresh"/>: the same search again
    /// because the index changed (keeps the selection and scroll position).
    /// </summary>
    void RunSearch(bool refresh)
    {
        var text = SearchBox.Text.Trim();
        var query = new SearchQuery(text, Kind);
        _cancel?.Cancel();
        var generation = ++_generation;
        if (query.IsEmpty && query.Kind == SearchKind.All)
        {
            _last = null;
            _list.Replace(new SearchResult(query, new()), null, false, null);
            Help.Visibility = Visibility.Visible;
            NoResults.Visibility = Visibility.Collapsed;
            CountText.Text = "";
            return;
        }
        Help.Visibility = Visibility.Collapsed;
        var within = !refresh && query.Narrows(_last?.Query) ? _last : null;
        var cancel = _cancel = new CancellationTokenSource();
        var sortBy = _sortBy;
        var started = Environment.TickCount64;
        Task.Run(() =>
        {
            var result = FileIndex.Search(query, cancel.Token, within);
            var (sorted, info, sortable) = Sort(result, sortBy, cancel.Token);
            return (result, sorted, info, sortable);
        }, cancel.Token).ContinueWith(task =>
        {
            if (generation != _generation || !task.IsCompletedSuccessfully) return;
            var (result, sorted, info, sortable) = task.Result;
            Show(result, sorted, info, refresh, sortable, Environment.TickCount64 - started);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void Show(SearchResult result, List<FileIndex.Hit>? sorted, Dictionary<FileIndex.Hit, (long, DateTime)>? info, bool refresh, bool sortable, long ms)
    {
        // A refresh that overtook a new search is that new search, as far as showing it goes.
        refresh = refresh && _last != null && _last.Query.Text == result.Query.Text && _last.Query.Kind == result.Query.Kind && !_resorted;
        _resorted = false;
        // Same results as before (nothing relevant changed): leave the list alone.
        if (refresh && _last != null && sorted == null && Same(_last, result)) return;

        var selected = refresh ? (List.SelectedItem as SearchRow)?.Path : null;
        var offset = refresh ? FindScroll()?.VerticalOffset : null;
        _last = result;
        _list.Replace(result, sorted, _descending, info);
        NoResults.Visibility = result.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoResults.Text = FileIndex.Running ? "Nothing matches that" : "File indexing is off. Turn on Search files in DeskShell's settings (General).";
        CountText.Text = (result.Count == 1 ? "1 result" : $"{result.Count:N0} results") + (sortable ? "" : $"  ·  too many to sort by {_sortBy.ToLowerInvariant()}");
        if (Environment.GetEnvironmentVariable("DESKSHELL_SEARCH_TIMING") == "1") CountText.Text += $"  ·  {ms} ms";
        if (_list.Count == 0) return;

        if (refresh)
        {
            if (offset is { } y) FindScroll()?.ScrollToVerticalOffset(y);
            // Keep the same file selected if it's still near where it was.
            if (selected != null)
                for (var i = 0; i < Math.Min(_list.Count, 400); i++)
                    if (string.Equals(_list.RowAt(i).Path, selected, StringComparison.OrdinalIgnoreCase))
                    {
                        List.SelectedIndex = i;
                        return;
                    }
            return;
        }
        List.SelectedIndex = 0;
        FindScroll()?.ScrollToTop();
    }

    static bool Same(SearchResult a, SearchResult b)
    {
        if (a.Count != b.Count || a.Parts.Length != b.Parts.Length) return false;
        for (var i = 0; i < a.Parts.Length; i++)
            if (a.Parts[i].Volume != b.Parts[i].Volume || !a.Parts[i].Slots.AsSpan().SequenceEqual(b.Parts[i].Slots)) return false;
        return true;
    }

    // ---------- sorting ----------

    void Header_Click(object sender, RoutedEventArgs e)
    {
        var by = (string)((Button)sender).Tag;
        if (by == _sortBy) _descending = !_descending;
        else
        {
            _sortBy = by;
            _descending = by is "Size" or "Modified"; // biggest and newest first
        }
        ArrowName.Text = ArrowFolder.Text = ArrowSize.Text = ArrowModified.Text = "";
        var arrow = _descending ? "" : "";
        (by switch { "Folder" => ArrowFolder, "Size" => ArrowSize, "Modified" => ArrowModified, _ => ArrowName }).Text = arrow;
        _resorted = true; // the same search, shown from the top in the new order
        RunSearch(refresh: true);
    }

    /// <summary>The results in another column's order (null for name order, which they come in already).</summary>
    static (List<FileIndex.Hit>?, Dictionary<FileIndex.Hit, (long, DateTime)>?, bool) Sort(SearchResult result, string by, CancellationToken cancel)
    {
        if (by == "Name" || result.Count == 0) return (null, null, true);
        if (result.Count > SortLimit) return (null, null, false);
        var hits = result.All().ToArray();
        var order = Enumerable.Range(0, hits.Length).ToArray();
        if (by == "Folder")
        {
            var folders = new string[hits.Length];
            ForEachLocked(hits, i => folders[i] = hits[i].Volume.FolderOf(hits[i].Slot));
            cancel.ThrowIfCancellationRequested();
            Array.Sort(order, (a, b) =>
            {
                var c = string.Compare(folders[a], folders[b], StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : a - b;
            });
            return (order.Select(i => hits[i]).ToList(), null, true);
        }

        var paths = new string[hits.Length];
        ForEachLocked(hits, i => paths[i] = hits[i].Volume.PathOf(hits[i].Slot));
        var sizes = new long[hits.Length];
        var dates = new DateTime[hits.Length];
        Parallel.For(0, hits.Length, new ParallelOptions { CancellationToken = cancel }, i =>
        {
            var (size, modified, _) = RowInfo.Read(paths[i]);
            sizes[i] = size;
            dates[i] = modified;
        });
        Array.Sort(order, (a, b) =>
        {
            var c = by == "Size" ? sizes[a].CompareTo(sizes[b]) : dates[a].CompareTo(dates[b]);
            return c != 0 ? c : a - b;
        });
        var info = new Dictionary<FileIndex.Hit, (long, DateTime)>(hits.Length);
        for (var i = 0; i < hits.Length; i++) info[hits[i]] = (sizes[i], dates[i]);
        return (order.Select(i => hits[i]).ToList(), info, true);
    }

    /// <summary>Runs <paramref name="action"/> for each hit, holding its drive's read lock (a drive at a time).</summary>
    static void ForEachLocked(FileIndex.Hit[] hits, Action<int> action)
    {
        foreach (var group in Enumerable.Range(0, hits.Length).GroupBy(i => hits[i].Volume))
        {
            group.Key.Lock.EnterReadLock();
            try
            {
                foreach (var i in group) action(i);
            }
            finally
            {
                group.Key.Lock.ExitReadLock();
            }
        }
    }

    // ---------- status ----------

    void UpdateStatus()
    {
        var text = FileIndex.Status();
        if (StatusText.Text != text) StatusText.Text = text;
    }

    // ---------- keyboard ----------

    List<SearchRow> Selected => List.SelectedItems.Cast<SearchRow>().OrderBy(r => r.Index).ToList();

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        var inBox = Keyboard.FocusedElement == SearchBox;
        var items = Selected;

        bool Handle(Action action)
        {
            action();
            e.Handled = true;
            return true;
        }

        switch (key)
        {
            case Key.Escape:
                Handle(HideWindow);
                return;
            case Key.Enter when mods == ModifierKeys.Control:
                Handle(() => ShowInFolder(items.FirstOrDefault()));
                return;
            case Key.Enter when mods == ModifierKeys.Alt:
                Handle(() => Properties(items));
                return;
            case Key.Enter when mods == ModifierKeys.None:
                Handle(() => Open(items));
                return;
            case Key.C when mods == (ModifierKeys.Control | ModifierKeys.Shift):
                Handle(() => CopyPaths(items));
                return;
            case Key.C when mods == ModifierKeys.Control && (!inBox || SearchBox.SelectionLength == 0) && items.Count > 0:
                Handle(() => FileOps.ToClipboard(items.Select(r => r.Path).ToList(), cut: false));
                return;
            case Key.F5:
                Handle(() => RunSearch(refresh: true));
                return;
            case Key.Apps:
            case Key.F10 when mods == ModifierKeys.Shift:
                Handle(() => ShowMenu(items, fromKeyboard: true, extended: false));
                return;
            case Key.Down or Key.Up or Key.PageDown or Key.PageUp when inBox && mods is ModifierKeys.None or ModifierKeys.Shift:
                Handle(() => Move(key, mods == ModifierKeys.Shift));
                return;
        }

        if (inBox) return;
        switch (key)
        {
            case Key.Space when mods == ModifierKeys.None && items.Count == 1:
                Handle(() => QuickLook(items[0]));
                return;
            case Key.Delete:
                Handle(() => Delete(items, permanently: mods == ModifierKeys.Shift));
                return;
            case Key.Up when List.SelectedIndex == 0 && mods == ModifierKeys.None:
                Handle(() => SearchBox.Focus());
                return;
        }
        // Typing while the list has the focus goes on in the box.
        if (mods is ModifierKeys.None or ModifierKeys.Shift && (key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or Key.Back or Key.OemPeriod or Key.Multiply))
            SearchBox.Focus();
    }

    /// <summary>Arrow keys in the box move through the results.</summary>
    void Move(Key key, bool extend)
    {
        if (_list.Count == 0) return;
        var page = Math.Max(1, (int)(List.ActualHeight / 32) - 1);
        var current = List.SelectedIndex < 0 ? -1 : List.SelectedIndex;
        var next = key switch
        {
            Key.Down => current + 1,
            Key.Up => current - 1,
            Key.PageDown => current + page,
            _ => current - page
        };
        next = Math.Clamp(next, 0, _list.Count - 1);
        if (extend && current >= 0)
        {
            var row = _list.RowAt(next);
            if (!List.SelectedItems.Contains(row)) List.SelectedItems.Add(row);
        }
        else List.SelectedIndex = next;
        List.ScrollIntoView(_list.RowAt(next));
    }

    // ---------- mouse ----------

    void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(List);
        _dragRow = (e.OriginalSource as DependencyObject) is { } source && ItemsControl.ContainerFromElement(List, source) is ListBoxItem item
            ? item.DataContext as SearchRow : null;
    }

    void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragRow == null || e.LeftButton != MouseButtonState.Pressed) return;
        var position = e.GetPosition(List);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var rows = List.SelectedItems.Contains(_dragRow) ? Selected : new List<SearchRow> { _dragRow };
        _dragRow = null;
        // Out to the desktop, an explorer window, a chat app…
        var data = new DataObject(DataFormats.FileDrop, rows.Select(r => r.Path).ToArray());
        _hold = true;
        try { DragDrop.DoDragDrop(List, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link); }
        catch (Exception ex) { Log.Error("Drag from search failed", ex); }
        finally { _hold = false; }
    }

    void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if ((e.OriginalSource as DependencyObject) is not { } source || ItemsControl.ContainerFromElement(List, source) is not ListBoxItem) return;
        Open(Selected);
    }

    void List_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        var fromMouse = e.CursorLeft >= 0;
        if (fromMouse && (e.OriginalSource as DependencyObject) is { } source && ItemsControl.ContainerFromElement(List, source) is ListBoxItem item &&
            !item.IsSelected)
        {
            List.SelectedItems.Clear();
            item.IsSelected = true;
        }
        ShowMenu(Selected, fromKeyboard: !fromMouse, extended: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    // ---------- actions ----------

    static bool UseDeskShellFiles => Settings.ReadOnly()?.UseDeskShellFiles ?? true;

    void Open(List<SearchRow> rows)
    {
        if (rows.Count == 0) return;
        foreach (var row in rows.Take(20))
        {
            if (row.IsFolder && UseDeskShellFiles) FilesHost.App?.ShowFolder(row.Path, null);
            else FileOps.Open(row.Path);
        }
        HideWindow();
    }

    void ShowInFolder(SearchRow? row)
    {
        if (row == null) return;
        if (UseDeskShellFiles) FilesHost.App?.ShowFolder(row.Folder, row.Path);
        else
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            try { System.Diagnostics.Process.Start(explorer, $"/select,\"{row.Path}\""); }
            catch (Exception ex) { Log.Error("Couldn't show the file in Explorer", ex); }
        }
        HideWindow();
    }

    static void CopyPaths(List<SearchRow> rows)
    {
        if (rows.Count == 0) return;
        var text = string.Join(Environment.NewLine, rows.Select(r => r.Path.Contains(' ') ? $"\"{r.Path}\"" : r.Path));
        try { Clipboard.SetText(text); }
        catch (Exception ex) { Log.Error("Couldn't copy paths", ex); }
    }

    void Properties(List<SearchRow> rows)
    {
        if (rows.Count == 0) return;
        _hold = true;
        try { FileOps.Properties(rows[0].Path); }
        finally { _hold = false; }
    }

    void Delete(List<SearchRow> rows, bool permanently)
    {
        if (rows.Count == 0) return;
        _hold = true;
        try { FileOps.Delete(_hwnd, rows.Select(r => r.Path).ToList(), permanently); }
        finally { _hold = false; }
        _refresh.Stop();
        _refresh.Start();
    }

    void QuickLook(SearchRow row)
    {
        if (row.IsFolder) return;
        if (_quickLook == null || _quickLook.IsClosed)
        {
            _quickLook = new QuickLookWindow();
            _quickLook.OpenRequested += path => Open(Selected.Where(r => r.Path == path).ToList());
            _quickLook.NavigateRequested += by =>
            {
                var next = Math.Clamp(List.SelectedIndex + by, 0, _list.Count - 1);
                List.SelectedIndex = next;
                List.ScrollIntoView(_list.RowAt(next));
                if (!_list.RowAt(next).IsFolder) _quickLook?.ShowFor(this, _list.RowAt(next).Path);
            };
        }
        if (_quickLook.IsVisible && _quickLook.CurrentPath == row.Path) _quickLook.HideLook();
        else _quickLook.ShowFor(this, row.Path);
    }

    /// <summary>
    /// The explorer's right-click menu: Windows' own (with Open with, Send to, 7-Zip…) and DeskShell's
    /// commands at the top. A selection spanning folders is narrowed to the first one's folder.
    /// </summary>
    void ShowMenu(List<SearchRow> rows, bool fromKeyboard, bool extended)
    {
        if (rows.Count == 0) return;
        var folder = rows[0].Folder;
        var inFolder = rows.Where(r => string.Equals(r.Folder, folder, StringComparison.OrdinalIgnoreCase)).ToList();
        var point = PointToScreen(Mouse.GetPosition(this));
        if (fromKeyboard && List.ItemContainerGenerator.ContainerFromItem(inFolder[^1]) is ListBoxItem container)
            point = container.PointToScreen(new Point(40, container.ActualHeight));
        _hold = true;
        try
        {
            ShellMenu.Show(_hwnd, folder, inFolder.Select(r => r.Path).ToList(), point, extended, verb =>
            {
                switch (verb.ToLowerInvariant())
                {
                    case "open" or "explore" or "opennewwindow" when inFolder.Count == 1 && inFolder[0].IsFolder && UseDeskShellFiles:
                        Open(inFolder);
                        return true;
                    default:
                        return false;
                }
            }, Extras(inFolder));
        }
        finally
        {
            _hold = false;
        }
        if (!IsActive && _quickLook is not { IsVisible: true } && IsVisible) Activate();
    }

    List<ShellMenu.Extra> Extras(List<SearchRow> rows)
    {
        var one = rows.Count == 1 ? rows[0] : null;
        var extras = new List<ShellMenu.Extra>
        {
            new("Open file location\tCtrl+Enter", () => ShowInFolder(rows[0]))
        };
        if (one is { IsFolder: false }) extras.Add(new("Quick Look\tSpace", () => QuickLook(one)));
        extras.Add(new("Copy as path\tCtrl+Shift+C", () => CopyPaths(rows)));
        extras.Add(ShellMenu.Extra.Separator);
        extras.Add(new("Compress to ZIP", () => _ = Run(() => Archive.CompressAsync(rows.Select(r => r.Path).ToList(), null, CancellationToken.None))));
        if (one != null && Archive.IsZip(one.Path)) extras.Add(new("Extract here", () => _ = Run(() => Archive.ExtractAsync(one.Path, null, CancellationToken.None))));
        if (one is { IsFolder: true })
        {
            extras.Add(ShellMenu.Extra.Separator);
            var pinned = FileView.IsPinned(one.Path);
            extras.Add(new(pinned ? "Unpin from sidebar" : "Pin to sidebar", () => FileView.SetPinned(one.Path, !pinned)));
            extras.Add(new("Open in Terminal", () => FileOps.TerminalHere(one.Path)));
        }
        return extras;
    }

    static async Task Run(Func<Task<string>> work)
    {
        try { await work(); }
        catch (Exception ex) { Log.Error("Search menu command failed", ex); }
    }

    ScrollViewer? FindScroll() => FindChild<ScrollViewer>(List);

    static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    // ---------- native ----------

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
