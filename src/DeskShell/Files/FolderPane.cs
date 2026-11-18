using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using IOPath = System.IO.Path;

namespace DeskShell.Files;

/// <summary>
/// One folder in a rounded card, for the explorer's dual-pane mode: back/up/refresh, a clickable
/// path, and a details list that reads the folder in the background and follows changes on disk.
/// It does no cross-pane work itself; copying to the other pane and the right-click menu are
/// raised as events so the window that owns both panes decides what happens.
/// </summary>
public sealed class FolderPane : Border
{
    const double ModifiedColumn = 132, SizeColumn = 84, NameInset = 37;

    readonly Button _backButton, _upButton;
    readonly Border _addressBar;
    readonly ScrollViewer _crumbScroller;
    readonly StackPanel _crumbs;
    readonly TextBox _addressBox, _renameBox;
    readonly ListBox _list;
    readonly Canvas _overlay;
    readonly TextBlock _note, _status;
    readonly Dictionary<string, TextBlock> _headerArrows = new();
    readonly Stack<string> _back = new();
    readonly DispatcherTimer _refreshTimer;

    ObservableCollection<FileItem> _items = new();
    string? _path, _error, _selectAfterRefresh;
    bool _loaded, _isActive;
    int _generation;
    CancellationTokenSource? _loading;
    FileSystemWatcher? _watcher;
    ScrollViewer? _scroller;
    string _sortBy;
    bool _descending;

    Point _dragStart;
    FileItem? _dragItem, _selectOnlyOnUp, _renaming, _lastFocused;

    /// <summary>Asks the host to show Windows' menu for these paths (empty = the folder itself) at this screen point (device pixels).</summary>
    public event Action<FolderPane, IReadOnlyList<string>, Point>? MenuRequested;
    /// <summary>Raised whenever the pane is clicked or gets keyboard focus, so the host can make it the active one.</summary>
    public event Action<FolderPane>? Activated;
    /// <summary>Raised when the pane starts showing a different folder.</summary>
    public event Action<FolderPane, string>? Navigated;
    /// <summary>F5 (copy, false) or F6 (move, true) of the selection to the other pane; the host does the transfer.</summary>
    public event Action<FolderPane, bool>? TransferRequested;

    public FolderPane()
    {
        CornerRadius = new CornerRadius(18);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(6);
        SetResourceReference(BackgroundProperty, "CardBrush");
        SetResourceReference(BorderBrushProperty, "EdgeBrush");
        SetResourceReference(TextElement.FontFamilyProperty, "UiFont");
        SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
        TextElement.SetFontSize(this, 13);

        var prefs = FileView.Prefs;
        _sortBy = prefs.SortBy is "Modified" or "Size" ? prefs.SortBy : "Name";
        _descending = prefs.SortDescending;

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            _ = RefreshAsync();
        };

        // ---------- header: buttons and the path ----------
        _backButton = RoundButton("\uE72B", "Back (Backspace, Alt+Left)", GoBack);
        _upButton = RoundButton("\uE74A", "Up (Alt+Up)", GoUp);
        var refreshButton = RoundButton("\uE72C", "Refresh (Ctrl+R)", Refresh);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { _backButton, _upButton, refreshButton } };

        _crumbs = new StackPanel { Orientation = Orientation.Horizontal };
        _crumbScroller = new ScrollViewer
        {
            Content = _crumbs,
            Margin = new Thickness(4, 0, 4, 0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false // so clicks on empty space reach the bar and start editing
        };
        _crumbScroller.PreviewMouseWheel += (_, e) =>
        {
            _crumbScroller.ScrollToHorizontalOffset(_crumbScroller.HorizontalOffset - e.Delta / 3.0);
            e.Handled = true;
        };
        _addressBox = new TextBox
        {
            Visibility = Visibility.Collapsed,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 0, 8, 0),
            FontSize = 13
        };
        _addressBox.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        _addressBox.SetResourceReference(TextBoxBase.CaretBrushProperty, "TextBrush");
        _addressBox.SetResourceReference(TextBoxBase.SelectionBrushProperty, "AccentBrush");
        _addressBox.PreviewKeyDown += AddressBox_PreviewKeyDown;
        _addressBox.LostKeyboardFocus += (_, _) => EndAddressEdit();

        _addressBar = new Border
        {
            Height = 32,
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = Cursors.IBeam,
            Child = new Grid { Children = { _crumbScroller, _addressBox } }
        };
        _addressBar.SetResourceReference(BackgroundProperty, "RaisedBrush");
        _addressBar.SetResourceReference(BorderBrushProperty, "EdgeBrush");
        _addressBar.MouseEnter += (_, _) => _addressBar.SetResourceReference(BackgroundProperty, "RaisedHoverBrush");
        _addressBar.MouseLeave += (_, _) => _addressBar.SetResourceReference(BackgroundProperty, "RaisedBrush");
        _addressBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && FindParent<ButtonBase>(source) != null) return;
            EditAddress();
            e.Handled = true;
        };

        var header = new Grid { Margin = new Thickness(2, 2, 2, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(buttons);
        Grid.SetColumn(_addressBar, 1);
        header.Children.Add(_addressBar);

        // ---------- column headers ----------
        var columns = new Grid { Margin = new Thickness(0, 0, 14, 2) }; // 14 lines up with the list's scroll bar
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ModifiedColumn) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SizeColumn) });
        var nameHeader = Header("Name", "Name", HorizontalAlignment.Left);
        nameHeader.Margin = new Thickness(NameInset - 10, 0, 0, 0);
        var modifiedHeader = Header("Date modified", "Modified", HorizontalAlignment.Left);
        var sizeHeader = Header("Size", "Size", HorizontalAlignment.Right);
        Grid.SetColumn(modifiedHeader, 1);
        Grid.SetColumn(sizeHeader, 2);
        columns.Children.Add(nameHeader);
        columns.Children.Add(modifiedHeader);
        columns.Children.Add(sizeHeader);
        UpdateHeaders();

        // ---------- the list ----------
        _list = new ListBox
        {
            SelectionMode = SelectionMode.Extended,
            AllowDrop = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FocusVisualStyle = null,
            IsTextSearchEnabled = true,
            ItemTemplate = RowTemplate,
            ItemContainerStyle = RowStyle
        };
        _list.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(_list, ScrollUnit.Pixel);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetCanContentScroll(_list, true);
        TextSearch.SetTextPath(_list, "Name");
        _list.SelectionChanged += (_, _) => UpdateStatus();
        _list.PreviewKeyDown += List_PreviewKeyDown;
        _list.PreviewMouseLeftButtonDown += List_PreviewMouseLeftButtonDown;
        _list.PreviewMouseMove += List_PreviewMouseMove;
        _list.PreviewMouseLeftButtonUp += List_PreviewMouseLeftButtonUp;
        _list.AddHandler(MouseRightButtonUpEvent, new MouseButtonEventHandler(List_MouseRightButtonUp), true);
        _list.DragOver += (_, e) =>
        {
            e.Effects = EffectFor(e, DropTarget(e));
            e.Handled = true;
        };
        _list.Drop += (_, e) => DropInto(e, DropTarget(e));
        _list.GotKeyboardFocus += (_, e) =>
        {
            if (e.NewFocus is ListBoxItem { DataContext: FileItem item }) _lastFocused = item;
        };
        // The rename box sits over a row; if the rows move under it, finish the rename.
        _list.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (_renaming != null && e.VerticalChange != 0) CommitRename(refocus: false);
        }));

        _note = new TextBlock
        {
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(24, 40, 24, 0),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        _note.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");

        _renameBox = new TextBox { Height = 26, FontSize = 13, Visibility = Visibility.Collapsed };
        _renameBox.SetResourceReference(StyleProperty, "Field");
        _renameBox.PreviewKeyDown += RenameBox_PreviewKeyDown;
        _renameBox.LostKeyboardFocus += (_, _) => CommitRename(refocus: false);
        _overlay = new Canvas { ClipToBounds = true, Children = { _renameBox } };

        var listArea = new Grid { Children = { _list, _note, _overlay } };

        // ---------- footer ----------
        _status = new TextBlock { FontSize = 12, Margin = new Thickness(12, 6, 12, 2), TextTrimming = TextTrimming.CharacterEllipsis };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(columns, 1);
        Grid.SetRow(listArea, 2);
        Grid.SetRow(_status, 3);
        root.Children.Add(header);
        root.Children.Add(columns);
        root.Children.Add(listArea);
        root.Children.Add(_status);
        Child = root;

        PreviewMouseDown += (_, e) =>
        {
            Activated?.Invoke(this);
            if (e.ChangedButton == MouseButton.XButton1)
            {
                GoBack();
                e.Handled = true;
            }
        };
        IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if ((bool)e.NewValue) Activated?.Invoke(this);
        };
        UpdateButtons();
    }

    // ---------- public API ----------

    /// <summary>The folder shown, or null before the first navigation.</summary>
    public string? Path => _path;

    /// <summary>The selected items' full paths, in list order.</summary>
    public IReadOnlyList<string> SelectedPaths => SelectedItems().Select(i => i.Path).ToList();

    /// <summary>The accent edge that marks the pane the keyboard and transfers act from.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            SetResourceReference(BorderBrushProperty, value ? "AccentBrush" : "EdgeBrush");
        }
    }

    /// <summary>When true, F5 asks the host to copy to the other pane (Total Commander style) instead of refreshing; Ctrl+R still refreshes.</summary>
    public bool CommanderKeys { get; set; }

    /// <summary>Shows a folder (a file path shows its folder with the file selected). Call on the UI thread.</summary>
    public Task NavigateAsync(string path)
    {
        var location = Resolve(path, _path) ?? path;
        string? select = null;
        try
        {
            if (!Directory.Exists(location) && File.Exists(location))
            {
                select = location;
                location = IOPath.GetDirectoryName(location) ?? location;
            }
        }
        catch
        {
            // a bad path shows its error in the list
        }
        return Go(location, remember: true, select);
    }

    /// <summary>Re-reads the folder, keeping the selection; after <see cref="Release"/> it loads it again.</summary>
    public void Refresh()
    {
        if (_path == null) return;
        if (!_loaded)
        {
            _ = LoadAsync(_path, null);
            return;
        }
        _refreshTimer.Stop();
        _ = RefreshAsync();
    }

    /// <summary>Stops watching the folder and drops its items while the pane is hidden; <see cref="Refresh"/> or NavigateAsync brings it back.</summary>
    public void Release()
    {
        _loading?.Cancel();
        _loading = null;
        _generation++;
        _refreshTimer.Stop();
        _watcher?.Dispose();
        _watcher = null;
        _renaming = null;
        _renameBox.Visibility = Visibility.Collapsed;
        EndAddressEdit();
        _list.ItemsSource = null;
        _items = new();
        _lastFocused = _dragItem = _selectOnlyOnUp = null;
        _loaded = false;
        _error = null;
        UpdateNote();
        _status.Text = "";
    }

    /// <summary>Puts keyboard focus on the selected (or last focused, or first) row without changing the selection.</summary>
    public void FocusList()
    {
        var item = _list.SelectedItem as FileItem;
        if (item == null && _lastFocused != null && _items.Contains(_lastFocused)) item = _lastFocused;
        item ??= _items.FirstOrDefault();
        if (item == null)
        {
            _list.Focus();
            return;
        }
        _list.ScrollIntoView(item);
        Dispatcher.InvokeAsync(() =>
        {
            if (_list.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container) container.Focus();
            else _list.Focus();
        }, DispatcherPriority.Loaded);
    }

    // ---------- navigation ----------

    Task Go(string location, bool remember, string? select = null)
    {
        if (remember && _path != null && !SamePath(_path, location))
            _back.Push(_path);
        return LoadAsync(location, select);
    }

    void GoBack()
    {
        if (_back.Count == 0) return;
        var target = _back.Pop();
        _ = Go(target, remember: false, ChildOf(target, _path));
    }

    void GoUp()
    {
        if (_path == null || ParentOf(_path) is not { } parent) return;
        _ = Go(parent, remember: true, ChildOf(parent, _path));
    }

    async Task LoadAsync(string location, string? select)
    {
        _loading?.Cancel();
        var cancel = new CancellationTokenSource();
        _loading = cancel;
        var generation = ++_generation;
        var hadFocus = IsKeyboardFocusWithin;
        _path = location;
        _loaded = false;
        _error = null;
        _renaming = null;
        _renameBox.Visibility = Visibility.Collapsed;
        EndAddressEdit();
        BuildCrumbs();
        UpdateButtons();
        Watch(location);
        Navigated?.Invoke(this, location);

        // Only say "Loading…" for slow folders, so fast ones don't flicker.
        var slow = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
        slow.Tick += (_, _) =>
        {
            slow.Stop();
            if (generation == _generation) _status.Text = "Loading…";
        };
        slow.Start();

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
        if (cancel.IsCancellationRequested || generation != _generation) return;

        var items = entries.Select(entry => new FileItem(location, entry)).ToList();
        items.Sort(Comparer);
        _items = new ObservableCollection<FileItem>(items);
        _list.ItemsSource = _items;
        _lastFocused = null;
        _loaded = true;
        _error = error;
        UpdateNote();
        UpdateStatus();

        var chosen = select == null ? null : items.FirstOrDefault(i => SamePath(i.Path, select));
        if (chosen != null)
        {
            _list.SelectedItem = chosen;
            _list.ScrollIntoView(chosen);
        }
        else Scroller?.ScrollToTop();

        // Keep the keyboard here if it was here (the row that was double-clicked has just gone away),
        // unless the user has moved on to something else meanwhile.
        if (IsKeyboardFocusWithin || (hadFocus && !FocusIsElsewhere())) FocusList();
    }

    bool FocusIsElsewhere() =>
        Keyboard.FocusedElement is Visual focused && focused is not Window && !IsAncestorOf(focused);

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

    /// <summary>Re-reads the folder and updates the list in place, so selection and scroll position survive.</summary>
    async Task RefreshAsync()
    {
        if (!_loaded || _path == null) return;
        var location = _path;
        var generation = _generation;
        List<FolderEntry> entries;
        try
        {
            entries = await Task.Run(() => ReadFolder(location, CancellationToken.None));
        }
        catch
        {
            // The folder was deleted or renamed away: fall back to the nearest folder that still exists.
            if (generation == _generation && !Directory.Exists(location) && ExistingParent(location) is { } parent)
                _ = Go(parent, remember: false);
            return;
        }
        if (generation != _generation || !_loaded) return;

        var existing = new Dictionary<string, FileItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _items) existing.TryAdd(item.Name, item);
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
        Sync(_items, desired);

        _error = null;
        UpdateNote();
        UpdateStatus();
        if (_selectAfterRefresh != null && _items.FirstOrDefault(i => SamePath(i.Path, _selectAfterRefresh)) is { } wanted)
        {
            _selectAfterRefresh = null;
            SelectOnly(wanted, focus: IsKeyboardFocusWithin);
        }
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

    void Watch(string location)
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            var watcher = new FileSystemWatcher(location)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes,
                InternalBufferSize = 64 * 1024
            };
            // Bursts of changes (a copy of many files) become one refresh, 200 ms after the last.
            void Changed() => Dispatcher.InvokeAsync(() =>
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
            // some drives can't be watched; Refresh still works
        }
    }

    void ScheduleRefresh()
    {
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    void UpdateButtons()
    {
        _backButton.IsEnabled = _back.Count > 0;
        _upButton.IsEnabled = _path != null && ParentOf(_path) != null;
    }

    void UpdateNote()
    {
        var text = _error ?? (_loaded && _items.Count == 0 ? "This folder is empty." : null);
        _note.Text = text ?? "";
        _note.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    void UpdateStatus()
    {
        if (!_loaded)
        {
            _status.Text = "";
            return;
        }
        var count = _items.Count;
        var selected = _list.SelectedItems.Cast<FileItem>().ToList();
        if (selected.Count == 0)
        {
            _status.Text = count == 1 ? "1 item" : $"{count:N0} items";
            return;
        }
        var bytes = selected.Where(i => !i.IsFolder).Sum(i => i.Size);
        _status.Text = $"{selected.Count:N0} of {count:N0} selected" + (bytes > 0 ? " · " + FileItem.FormatSize(bytes) : "");
    }

    /// <summary>Shows a short message (a failed rename, an unknown path) in the footer until the next update.</summary>
    void ShowMessage(string text) => _status.Text = text;

    // ---------- the path bar ----------

    void BuildCrumbs()
    {
        _crumbs.Children.Clear();
        if (_path == null) return;
        var parts = new List<(string Name, string Path)>();
        var root = IOPath.GetPathRoot(_path);
        if (string.IsNullOrEmpty(root)) parts.Add((_path, _path));
        else
        {
            parts.Add((root.StartsWith(@"\\") ? root.TrimEnd('\\') : DriveName(root), root.EndsWith('\\') ? root : root + "\\"));
            var path = root.TrimEnd('\\');
            foreach (var part in _path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                path += "\\" + part;
                parts.Add((part, path));
            }
        }
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0)
            {
                var chevron = new TextBlock { Text = "\uE76C", FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 1, 1, 0) };
                chevron.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
                chevron.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
                _crumbs.Children.Add(chevron);
            }
            var target = parts[i].Path;
            var button = new Button
            {
                Width = double.NaN,
                Height = 26,
                Focusable = false,
                AllowDrop = true,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Content = new TextBlock { Text = parts[i].Name, Margin = new Thickness(8, 0, 8, 0), FontSize = 12.5 }
            };
            button.SetResourceReference(StyleProperty, "GhostButton");
            button.Loaded += (_, _) => Round(button);
            button.Click += (_, _) => _ = Go(target, remember: true);
            button.DragOver += (_, e) =>
            {
                e.Effects = EffectFor(e, target);
                e.Handled = true;
            };
            button.Drop += (_, e) => DropInto(e, target);
            _crumbs.Children.Add(button);
        }
        Dispatcher.InvokeAsync(() => _crumbScroller.ScrollToRightEnd(), DispatcherPriority.Loaded);
    }

    void EditAddress()
    {
        _addressBox.Text = _path ?? "";
        _addressBox.Visibility = Visibility.Visible;
        _crumbScroller.Visibility = Visibility.Hidden;
        _addressBox.Focus();
        _addressBox.SelectAll();
    }

    void EndAddressEdit()
    {
        _addressBox.Visibility = Visibility.Collapsed;
        _crumbScroller.Visibility = Visibility.Visible;
    }

    void AddressBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            EndAddressEdit();
            FocusList();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var text = _addressBox.Text.Trim();
            var target = Resolve(text, _path);
            if (target != null && Directory.Exists(target))
            {
                EndAddressEdit();
                _ = Go(target, remember: true);
                FocusList();
            }
            else if (target != null && File.Exists(target))
            {
                EndAddressEdit();
                FileOps.Open(target);
                FocusList();
            }
            else
            {
                ShowMessage($"Windows can't find \"{text}\".");
                _addressBox.SelectAll();
            }
        }
    }

    // ---------- sorting ----------

    FileComparer Comparer => new(_sortBy, _descending);

    void SortBy(string by)
    {
        if (_sortBy == by) _descending = !_descending;
        else
        {
            _sortBy = by;
            _descending = false;
        }
        UpdateHeaders();
        var sorted = _items.ToList();
        sorted.Sort(Comparer);
        Sync(_items, sorted);
    }

    void UpdateHeaders()
    {
        foreach (var (by, arrow) in _headerArrows)
        {
            arrow.Visibility = by == _sortBy ? Visibility.Visible : Visibility.Collapsed;
            arrow.Text = _descending ? "\uE70D" : "\uE70E";
        }
    }

    Button Header(string label, string by, HorizontalAlignment align)
    {
        var arrow = new TextBlock { FontSize = 8, Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        arrow.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        _headerArrows[by] = arrow;
        var button = new Button
        {
            Style = HeaderStyle,
            HorizontalContentAlignment = align,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = label }, arrow } }
        };
        button.Click += (_, _) => SortBy(by);
        return button;
    }

    // ---------- keys ----------

    void List_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        switch (key)
        {
            case Key.Enter when mods == ModifierKeys.None:
                OpenItems(Targets());
                break;
            case Key.Back when mods == ModifierKeys.None:
            case Key.Left when mods == ModifierKeys.Alt:
                GoBack();
                break;
            case Key.Up when mods == ModifierKeys.Alt:
                GoUp();
                break;
            case Key.F5 when mods == ModifierKeys.None:
                if (CommanderKeys) TransferRequested?.Invoke(this, false);
                else Refresh();
                break;
            case Key.F6 when mods == ModifierKeys.None:
                TransferRequested?.Invoke(this, true);
                break;
            case Key.R when mods == ModifierKeys.Control:
                Refresh();
                break;
            case Key.Delete when mods is ModifierKeys.None or ModifierKeys.Shift:
                var doomed = SelectedPaths;
                if (doomed.Count > 0) FileOps.Delete(Owner, doomed, permanently: mods == ModifierKeys.Shift);
                break;
            case Key.F2 when mods == ModifierKeys.None:
                StartRename();
                break;
            case Key.C when mods == ModifierKeys.Control:
                FileOps.ToClipboard(SelectedPaths, cut: false);
                break;
            case Key.X when mods == ModifierKeys.Control:
                FileOps.ToClipboard(SelectedPaths, cut: true);
                break;
            case Key.V when mods == ModifierKeys.Control:
                if (_path != null) FileOps.Paste(Owner, _path);
                break;
            case Key.Apps:
            case Key.F10 when mods == ModifierKeys.Shift:
                RequestMenuFromKeyboard();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Folders open in this pane; files open with their apps.</summary>
    void OpenItems(IReadOnlyList<FileItem> items)
    {
        if (items.Count == 0) return;
        var files = items.Where(i => !i.IsFolder).ToList();
        if (files.Count == 0)
        {
            _ = Go(items[0].Path, remember: true);
            return;
        }
        foreach (var file in files.Take(15)) FileOps.Open(file.Path);
    }

    List<FileItem> SelectedItems()
    {
        if (_list.SelectedItems.Count == 0) return new();
        var selected = new HashSet<FileItem>(_list.SelectedItems.Cast<FileItem>());
        return _items.Where(selected.Contains).ToList();
    }

    /// <summary>The selection, or the row with the keyboard focus if nothing is selected.</summary>
    List<FileItem> Targets()
    {
        var selected = SelectedItems();
        if (selected.Count > 0) return selected;
        return Keyboard.FocusedElement is ListBoxItem { DataContext: FileItem item } && _items.Contains(item) ? new List<FileItem> { item } : new List<FileItem>();
    }

    void SelectOnly(FileItem item, bool focus = true)
    {
        _list.SelectedItems.Clear();
        _list.SelectedItem = item;
        _list.ScrollIntoView(item);
        if (focus) FocusList();
    }

    IntPtr Owner => PresentationSource.FromVisual(this) is HwndSource source ? source.Handle : IntPtr.Zero;

    // ---------- renaming ----------

    void StartRename()
    {
        var item = Targets().FirstOrDefault();
        if (item == null) return;
        SelectOnly(item, focus: false);
        Dispatcher.InvokeAsync(() =>
        {
            if (_list.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container) return;
            var at = container.TranslatePoint(new Point(0, 0), _overlay);
            Canvas.SetLeft(_renameBox, at.X + NameInset - 9);
            Canvas.SetTop(_renameBox, at.Y + (container.ActualHeight - _renameBox.Height) / 2);
            _renameBox.Width = Math.Max(80, container.ActualWidth - ModifiedColumn - SizeColumn - NameInset + 9);
            _renameBox.Text = item.Name;
            _renaming = item;
            _renameBox.Visibility = Visibility.Visible;
            _renameBox.Focus();
            // Like Explorer: the name is selected, not the extension.
            var dot = item.IsFolder ? -1 : item.Name.LastIndexOf('.');
            if (dot > 0) _renameBox.Select(0, dot);
            else _renameBox.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    void RenameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitRename(refocus: true);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _renaming = null;
            _renameBox.Visibility = Visibility.Collapsed;
            FocusList();
        }
    }

    void CommitRename(bool refocus)
    {
        var item = _renaming;
        if (item == null) return;
        _renaming = null; // hiding the box loses focus, which comes back here
        var name = _renameBox.Text.Trim();
        _renameBox.Visibility = Visibility.Collapsed;
        var error = FileOps.Rename(item.Path, name);
        if (error != null) ShowMessage(error);
        else if (name.Length > 0 && name != item.Name)
        {
            _selectAfterRefresh = IOPath.Join(item.Folder, name);
            ScheduleRefresh(); // the watcher would too, but not every drive can be watched
        }
        if (refocus) FocusList();
    }

    // ---------- mouse, dragging and dropping ----------

    void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragItem = null;
        _selectOnlyOnUp = null;
        if (e.OriginalSource is DependencyObject source && FindParent<ScrollBar>(source) != null) return;
        var container = ContainerAt(e.OriginalSource);
        if (container == null)
        {
            // Empty space: like Explorer, a plain click clears the selection.
            if (Keyboard.Modifiers == ModifierKeys.None) _list.UnselectAll();
            _list.Focus();
            e.Handled = true;
            return;
        }
        if (container.DataContext is not FileItem item) return;
        if (e.ClickCount == 2)
        {
            OpenItems(new[] { item });
            e.Handled = true;
            return;
        }
        _dragStart = e.GetPosition(_list);
        _dragItem = item;
        // Pressing on one of several selected items keeps them all selected, so they can be dragged together.
        if (container.IsSelected && Keyboard.Modifiers == ModifierKeys.None && _list.SelectedItems.Count > 1)
        {
            _selectOnlyOnUp = item;
            container.Focus();
            e.Handled = true;
        }
    }

    void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        var position = e.GetPosition(_list);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var selected = SelectedItems();
        var items = selected.Contains(_dragItem) ? selected : new List<FileItem> { _dragItem };
        _dragItem = null;
        _selectOnlyOnUp = null;
        var data = new DataObject(DataFormats.FileDrop, items.Select(i => i.Path).ToArray());
        try { DragDrop.DoDragDrop(_list, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link); }
        catch (Exception ex) { Log.Error("Drag failed", ex); }
    }

    void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_selectOnlyOnUp != null) SelectOnly(_selectOnlyOnUp);
        _selectOnlyOnUp = null;
        _dragItem = null;
    }

    void List_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindParent<ScrollBar>(source) != null) return;
        e.Handled = true;
        IReadOnlyList<string> paths;
        if (ContainerAt(e.OriginalSource) is { DataContext: FileItem item } container)
        {
            if (!container.IsSelected) SelectOnly(item);
            paths = SelectedPaths;
        }
        else
        {
            _list.UnselectAll();
            paths = Array.Empty<string>();
        }
        MenuRequested?.Invoke(this, paths, PointToScreen(e.GetPosition(this)));
    }

    void RequestMenuFromKeyboard()
    {
        var items = Targets();
        Point screen;
        if (items.Count > 0 && _list.ItemContainerGenerator.ContainerFromItem(items[0]) is ListBoxItem { IsVisible: true } container)
            screen = container.PointToScreen(new Point(Math.Min(40, container.ActualWidth), container.ActualHeight));
        else screen = _list.PointToScreen(new Point(40, 10));
        if (items.Count > 0 && _list.SelectedItems.Count == 0) _list.SelectedItem = items[0];
        MenuRequested?.Invoke(this, items.Select(i => i.Path).ToList(), screen);
    }

    /// <summary>A folder row under the pointer, otherwise the folder shown.</summary>
    string? DropTarget(DragEventArgs e) =>
        ContainerAt(e.OriginalSource)?.DataContext is FileItem { IsFolder: true } folder ? folder.Path : _path;

    static string[]? DroppedFiles(IDataObject data)
    {
        try { return data.GetData(DataFormats.FileDrop) as string[]; }
        catch { return null; } // some apps offer data that can't be read
    }

    /// <summary>Like Explorer: Ctrl copies, Shift moves, otherwise move on the same drive and copy across drives.</summary>
    static DragDropEffects EffectFor(DragEventArgs e, string? target)
    {
        if (target == null || DroppedFiles(e.Data) is not { Length: > 0 } files) return DragDropEffects.None;
        var into = target.TrimEnd('\\');
        if (files.Any(f => string.Equals(f.TrimEnd('\\'), into, StringComparison.OrdinalIgnoreCase))) return DragDropEffects.None;
        var sameFolder = files.All(f => string.Equals(IOPath.GetDirectoryName(f.TrimEnd('\\'))?.TrimEnd('\\'), into, StringComparison.OrdinalIgnoreCase));
        DragDropEffects wanted;
        if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0) wanted = DragDropEffects.Copy;
        else if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0) wanted = DragDropEffects.Move;
        else
        {
            if (sameFolder) return DragDropEffects.None;
            var sameDrive = files.All(f => string.Equals(IOPath.GetPathRoot(f), IOPath.GetPathRoot(target), StringComparison.OrdinalIgnoreCase));
            wanted = sameDrive ? DragDropEffects.Move : DragDropEffects.Copy;
        }
        if (sameFolder && wanted == DragDropEffects.Move) return DragDropEffects.None;
        return (e.AllowedEffects & wanted) != 0 ? wanted : (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
    }

    void DropInto(DragEventArgs e, string? target)
    {
        e.Handled = true;
        var effect = EffectFor(e, target);
        if (effect == DragDropEffects.None || target == null || DroppedFiles(e.Data) is not { } files) return;
        if (effect == DragDropEffects.Move) FileOps.Move(Owner, files, target);
        else FileOps.Copy(Owner, files, target);
        e.Effects = effect;
    }

    // ---------- helpers ----------

    Button RoundButton(string glyph, string tip, Action click)
    {
        var icon = new TextBlock { Text = glyph, FontSize = 13 };
        icon.SetResourceReference(StyleProperty, "Icon");
        var button = new Button { Width = 32, Height = 32, Content = icon, ToolTip = tip, Focusable = false };
        button.SetResourceReference(StyleProperty, "GhostButton");
        button.Click += (_, _) => click();
        button.Loaded += (_, _) => Round(button);
        return button;
    }

    /// <summary>GhostButton has square-ish corners; the pane's buttons are pills, so round its background once it exists.</summary>
    static void Round(Control control)
    {
        control.ApplyTemplate();
        if (control.Template?.FindName("Bg", control) is Border bg) bg.CornerRadius = new CornerRadius(control.ActualHeight > 0 ? control.ActualHeight / 2 : 13);
    }

    ScrollViewer? Scroller => _scroller ??= FindChild<ScrollViewer>(_list);

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

    static bool SamePath(string a, string b) => string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Turns typed text (environment variables, shell: names, relative paths) into a full folder path.</summary>
    static string? Resolve(string input, string? relativeTo)
    {
        var text = Environment.ExpandEnvironmentVariables(input.Trim().Trim('"'));
        if (text.Length == 0) return null;
        try
        {
            if (text.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("::"))
                return ShellNative.ShellPathToFolder(text) is { } folder ? Normalize(folder) : null;
            if (!IOPath.IsPathRooted(text) && relativeTo != null) text = IOPath.Combine(relativeTo, text);
            return Normalize(text);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Full path without a trailing slash, except for drive roots ("C:\").</summary>
    static string Normalize(string path)
    {
        path = IOPath.GetFullPath(path);
        var root = IOPath.GetPathRoot(path);
        if (root != null && string.Equals(path.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return root.EndsWith('\\') ? root : root + "\\";
        return path.TrimEnd('\\');
    }

    static string? ParentOf(string path)
    {
        try { return new DirectoryInfo(path).Parent is { } parent ? Normalize(parent.FullName) : null; }
        catch { return null; }
    }

    static string? ExistingParent(string path)
    {
        for (var parent = ParentOf(path); parent != null; parent = ParentOf(parent))
            if (Directory.Exists(parent)) return parent;
        return null;
    }

    /// <summary>The child of <paramref name="parent"/> that leads to <paramref name="current"/>, so going up or back selects where you came from.</summary>
    static string? ChildOf(string parent, string? current)
    {
        if (current == null) return null;
        var prefix = parent.TrimEnd('\\') + "\\";
        if (!current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || current.Length <= prefix.Length) return null;
        var rest = current[prefix.Length..];
        var slash = rest.IndexOf('\\');
        return prefix + (slash < 0 ? rest : rest[..slash]);
    }

    static readonly Dictionary<string, string> DriveNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"Local Disk (C:)" rather than "C:\", looked up once per drive.</summary>
    static string DriveName(string root)
    {
        if (DriveNames.TryGetValue(root, out var known)) return known;
        var info = new ShellNative.SHFILEINFO();
        var name = ShellNative.SHGetFileInfo(root, 0, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf<ShellNative.SHFILEINFO>(), 0x200 /* SHGFI_DISPLAYNAME */) != IntPtr.Zero
                   && !string.IsNullOrEmpty(info.szDisplayName)
            ? info.szDisplayName
            : root.TrimEnd('\\');
        DriveNames[root] = name;
        return name;
    }

    // ---------- templates, parsed once and shared by every pane ----------

    const string Xmlns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

    static DataTemplate? _rowTemplate;
    static Style? _rowStyle, _headerStyle;

    /// <summary>Icon and name, date modified, size; like the main window's details rows, minus the type column.</summary>
    static DataTemplate RowTemplate => _rowTemplate ??= (DataTemplate)XamlReader.Parse($$"""
        <DataTemplate {{Xmlns}}>
            <Grid Height="28" Background="Transparent">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="{{ModifiedColumn}}" />
                    <ColumnDefinition Width="{{SizeColumn}}" />
                </Grid.ColumnDefinitions>
                <Grid Margin="10,0,6,0" Opacity="{Binding Faded}">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="Auto" />
                        <ColumnDefinition Width="*" />
                    </Grid.ColumnDefinitions>
                    <Image Source="{Binding Icon}" Width="18" Height="18" Margin="0,0,9,0" RenderOptions.BitmapScalingMode="HighQuality" />
                    <TextBlock Grid.Column="1" Text="{Binding DisplayName}" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
                </Grid>
                <TextBlock Grid.Column="1" Text="{Binding ModifiedText}" Foreground="{DynamicResource TextDimBrush}" VerticalAlignment="Center"
                           Margin="10,0" FontSize="12" TextTrimming="CharacterEllipsis" />
                <TextBlock Grid.Column="2" Text="{Binding SizeText}" Foreground="{DynamicResource TextDimBrush}" VerticalAlignment="Center"
                           Margin="6,0,12,0" FontSize="12" HorizontalAlignment="Right" />
            </Grid>
        </DataTemplate>
        """);

    static Style RowStyle => _rowStyle ??= (Style)XamlReader.Parse($$"""
        <Style TargetType="ListBoxItem" {{Xmlns}}>
            <Setter Property="Foreground" Value="{DynamicResource TextBrush}" />
            <Setter Property="FocusVisualStyle" Value="{x:Null}" />
            <Setter Property="HorizontalContentAlignment" Value="Stretch" />
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="ListBoxItem">
                        <Grid>
                            <Border x:Name="Bg" Background="Transparent" CornerRadius="8" Margin="0,1" BorderThickness="1" BorderBrush="Transparent">
                                <ContentPresenter />
                            </Border>
                            <Border x:Name="Pill" Width="3" Height="14" CornerRadius="1.5" HorizontalAlignment="Left"
                                    Background="{DynamicResource AccentBrush}" Visibility="Collapsed" />
                        </Grid>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="Bg" Property="Background" Value="{DynamicResource RaisedBrush}" />
                            </Trigger>
                            <Trigger Property="IsSelected" Value="True">
                                <Setter TargetName="Bg" Property="Background" Value="{DynamicResource RaisedHoverBrush}" />
                                <Setter TargetName="Pill" Property="Visibility" Value="Visible" />
                            </Trigger>
                            <Trigger Property="IsKeyboardFocused" Value="True">
                                <Setter TargetName="Bg" Property="BorderBrush" Value="{DynamicResource EdgeBrush}" />
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        """);

    static Style HeaderStyle => _headerStyle ??= (Style)XamlReader.Parse($$"""
        <Style TargetType="Button" {{Xmlns}}>
            <Setter Property="Foreground" Value="{DynamicResource TextDimBrush}" />
            <Setter Property="Cursor" Value="Hand" />
            <Setter Property="Focusable" Value="False" />
            <Setter Property="FocusVisualStyle" Value="{x:Null}" />
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border x:Name="Bg" Background="Transparent" CornerRadius="10" Padding="10,4">
                            <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" TextElement.FontSize="12" />
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="Bg" Property="Background" Value="{DynamicResource RaisedBrush}" />
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        """);
}
