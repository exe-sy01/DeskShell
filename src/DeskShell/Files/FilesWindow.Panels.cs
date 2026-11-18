using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DeskShell.Files;

/// <summary>
/// The explorer's extra panels and tools: the preview / details panel on the right, a second
/// folder beside the first (two panes), Quick Look, zip, renaming many files at once and folder sizes.
/// </summary>
public partial class FilesWindow
{
    /// <summary>"preview", "details" or null.</summary>
    string? _sidePanel;
    PreviewPane? _preview;
    FolderPane? _dual;
    QuickLookWindow? _quickLook;
    DispatcherTimer? _previewTimer;
    CancellationTokenSource? _sizes;
    bool _mainActive = true;

    /// <summary>Puts the panels back as they were last time.</summary>
    void RestorePanels()
    {
        if (FileView.Prefs.SidePanel is "preview" or "details") SetSidePanel(FileView.Prefs.SidePanel, remember: false);
    }

    // ---------- preview / details ----------

    void SetSidePanel(string? which, bool remember = true)
    {
        _sidePanel = which;
        _syncing = true;
        PreviewToggle.IsChecked = which == "preview";
        InfoToggle.IsChecked = which == "details";
        _syncing = false;
        if (which == null)
        {
            _preview?.Clear();
            SideHost.Child = null;
            SideColumn.Width = new GridLength(0);
        }
        else
        {
            if (_preview == null)
            {
                _preview = new PreviewPane();
                _preview.ModeChanged += () =>
                {
                    // The panel's own Preview / Details switch
                    _sidePanel = _preview.ShowDetails ? "details" : "preview";
                    _syncing = true;
                    PreviewToggle.IsChecked = _sidePanel == "preview";
                    InfoToggle.IsChecked = _sidePanel == "details";
                    _syncing = false;
                    FileView.Prefs.SidePanel = _sidePanel;
                    FileView.Save();
                };
            }
            _preview.ShowDetails = which == "details";
            SideHost.Child = _preview;
            SideColumn.Width = new GridLength(Math.Clamp(FileView.Prefs.SidePanelWidth, 240, 600));
            SchedulePreview();
        }
        if (remember)
        {
            FileView.Prefs.SidePanel = which;
            FileView.Save();
        }
    }

    // The switches follow their own checked state (so keyboard and screen readers work too); _syncing marks our own updates.
    bool _syncing;
    void PreviewToggle_Click(object sender, RoutedEventArgs e) { if (!_syncing) SetSidePanel(PreviewToggle.IsChecked == true ? "preview" : _sidePanel == "preview" ? null : _sidePanel); }
    void InfoToggle_Click(object sender, RoutedEventArgs e) { if (!_syncing) SetSidePanel(InfoToggle.IsChecked == true ? "details" : _sidePanel == "details" ? null : _sidePanel); }

    /// <summary>The panel follows the selection, a moment after it settles (arrowing through a list shouldn't decode every picture).</summary>
    void SchedulePreview()
    {
        if (_preview == null || _sidePanel == null) return;
        _previewTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(140), DispatcherPriority.Background, (_, _) =>
        {
            _previewTimer!.Stop();
            if (_preview == null || _sidePanel == null) return;
            var paths = _mainActive || _dual == null ? Selected.Select(i => i.Path).ToList() : _dual.SelectedPaths.ToList();
            _preview.Show(paths);
        }, Dispatcher);
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    // ---------- Quick Look ----------

    void QuickLook(FileItem item)
    {
        if (_quickLook == null || _quickLook.IsClosed)
        {
            _quickLook = new QuickLookWindow();
            _quickLook.OpenRequested += path => FileOps.Open(path);
            _quickLook.NavigateRequested += delta =>
            {
                // Arrow keys in Quick Look move through the list.
                var shown = List.Items.Cast<FileItem>().ToList();
                var current = Selected.FirstOrDefault();
                var index = current == null ? 0 : shown.IndexOf(current) + delta;
                if (index < 0 || index >= shown.Count) return;
                var next = shown[index];
                SelectOnly(next);
                List.ScrollIntoView(next);
                if (!next.IsFolder) _quickLook.ShowFor(this, next.Path);
            };
            Closed += (_, _) => _quickLook?.Close();
        }
        if (item.IsFolder) return;
        _quickLook.ShowFor(this, item.Path);
    }

    // ---------- two panes ----------

    void DualToggle_Click(object sender, RoutedEventArgs e) { if (!_syncing && (DualToggle.IsChecked == true) != (_dual != null)) ToggleDual(); }

    void ToggleDual()
    {
        if (_dual != null)
        {
            _dual.Release();
            DualHost.Content = null;
            _dual = null;
            DualColumn.Width = new GridLength(0);
            _syncing = true;
            DualToggle.IsChecked = false;
            _syncing = false;
            _mainActive = true;
            return;
        }
        _dual = new FolderPane { CommanderKeys = true }; // F5 copies, F6 moves to the main pane
        _dual.MenuRequested += (pane, paths, point) =>
        {
            if (pane.Path == null) return;
            ShellMenu.Show(_hwnd, pane.Path, paths, point, extended: false, _ => false);
        };
        _dual.TransferRequested += (pane, move) =>
        {
            // F5 / F6 in the second pane: to the main pane's folder
            if (CurrentFolder is { } target && pane.SelectedPaths.Count > 0)
            {
                if (move) FileOps.Move(_hwnd, pane.SelectedPaths, target);
                else FileOps.Copy(_hwnd, pane.SelectedPaths, target);
            }
        };
        _dual.Activated += pane =>
        {
            if (!_mainActive && pane.IsActive) return;
            _mainActive = false;
            pane.IsActive = true;
            SchedulePreview();
        };
        List.GotKeyboardFocus += (_, _) => MarkMainActive();
        List.PreviewMouseDown += (_, _) => MarkMainActive();
        DualHost.Content = _dual;
        DualColumn.Width = new GridLength(1, GridUnitType.Star);
        _syncing = true;
        DualToggle.IsChecked = true;
        _syncing = false;
        _ = _dual.NavigateAsync(CurrentFolder ?? FileView.Downloads);
    }

    void MarkMainActive()
    {
        _mainActive = true;
        if (_dual != null) _dual.IsActive = false;
    }

    /// <summary>Copies (or moves) to the other pane's folder, through Windows' usual copy dialog.</summary>
    void TransferToOther(IReadOnlyList<string> paths, bool move)
    {
        if (_dual?.Path is not { } target || paths.Count == 0) return;
        if (move) FileOps.Move(_hwnd, paths, target);
        else FileOps.Copy(_hwnd, paths, target);
    }

    async Task OtherPaneOpen(string path)
    {
        if (_dual == null) ToggleDual();
        await _dual!.NavigateAsync(path);
    }

    // ---------- zip, rename all ----------

    async void CompressItems(List<FileItem> items)
    {
        var paths = items.Select(i => i.Path).ToList();
        try
        {
            var progress = new Progress<string>(text => Status.Text = text);
            var zip = await Archive.CompressAsync(paths, progress, CancellationToken.None);
            await RefreshAsync(_tab);
            SelectPath(zip);
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't make the zip", ex);
            Status.Text = "Couldn't make the zip: " + ex.Message;
        }
    }

    async void ExtractZip(string zip)
    {
        try
        {
            var progress = new Progress<string>(text => Status.Text = text);
            var folder = await Archive.ExtractAsync(zip, progress, CancellationToken.None);
            await RefreshAsync(_tab);
            SelectPath(folder);
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't extract the zip", ex);
            Status.Text = "Couldn't extract: " + ex.Message;
        }
    }

    async void BatchRename(List<FileItem> items)
    {
        var dialog = new BatchRenameWindow(items.Select(i => i.Path).ToList()) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await RefreshAsync(_tab);
        List.SelectedItems.Clear();
        foreach (var item in _tab.Shown.Where(i => dialog.RenamedPaths.Contains(i.Path, StringComparer.OrdinalIgnoreCase)))
            List.SelectedItems.Add(item);
    }

    void SelectPath(string path)
    {
        var item = _tab.Shown.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
        if (item == null) return;
        SelectOnly(item);
        List.ScrollIntoView(item);
    }

    // ---------- folder sizes ----------

    void ToggleFolderSizes()
    {
        FileView.Prefs.FolderSizes = !FileView.Prefs.FolderSizes;
        FileView.Save();
        if (FileView.Prefs.FolderSizes) MeasureFolders();
        else
        {
            _sizes?.Cancel();
            foreach (var item in _tab.Items.Where(i => i.IsFolder)) item.FolderBytes = null;
        }
    }

    /// <summary>With Folder sizes on, works out each folder's size in the shown folder, one at a time, in the background.</summary>
    async void MeasureFolders()
    {
        _sizes?.Cancel();
        if (!FileView.Prefs.FolderSizes || _tab.Location == Home) return;
        var cancel = _sizes = new CancellationTokenSource();
        var folders = _tab.Items.Where(i => i.IsFolder).ToList();
        foreach (var folder in folders)
        {
            if (FolderSize.TryCached(folder.Path, out var known))
            {
                folder.FolderBytes = known.Item1;
                continue;
            }
            folder.Measuring = true;
        }
        foreach (var folder in folders.Where(f => f.FolderBytes == null))
        {
            if (cancel.IsCancellationRequested) return;
            try
            {
                var result = await FolderSize.MeasureAsync(folder.Path, cancel.Token);
                folder.FolderBytes = result.Bytes;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                folder.Measuring = false;
            }
        }
    }
}
