using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using DeskShell.Services;
using ManagedShell;
using ManagedShell.AppBar;
using ManagedShell.WindowsTasks;

namespace DeskShell;

/// <summary>
/// The slim dock at the bottom of a monitor: the shell button, pinned items, then open windows.
/// It keeps its strip of the screen free (see WorkAreaReserver) and hides itself while a
/// fullscreen app (a game) is running.
/// </summary>
public partial class DockWindow : AppBarWindow
{
    readonly ICollectionView? _windows;

    public DockWindow(ShellManager shell, AppBarScreen screen, double height)
        // AppBarMode.None: the dock floats instead of registering as an app bar; WorkAreaReserver
        // keeps its strip free. (Registering made Explorer's hidden taskbar flicker on startup.)
        : base(shell.AppBarManager, shell.ExplorerHelper, shell.FullScreenHelper, screen, AppBarEdge.Bottom, AppBarMode.None, height)
    {
        InitializeComponent();

        _windows = shell.Tasks.CreateGroupedWindowsCollection();
        if (_windows != null)
        {
            _windows.Filter = IncludeWindow;
            if (_windows is ICollectionViewLiveShaping live)
            {
                live.IsLiveFiltering = true;
                live.LiveFilteringProperties.Add(nameof(ApplicationWindow.ShowInTaskbar));
                live.LiveFilteringProperties.Add(nameof(ApplicationWindow.HMonitor));
            }
        }
        WindowList.ItemsSource = _windows;
        PinList.ItemsSource = DockPins.Items;
        if (_windows is System.Collections.Specialized.INotifyCollectionChanged windowChanges)
            windowChanges.CollectionChanged += (_, _) => UpdatePinDivider();
        UpdatePinDivider();

        DockPins.Changed += OnPinsChanged;
        if (App.Fullscreen != null) App.Fullscreen.Changed += UpdateFullscreen;
        Closed += (_, _) =>
        {
            DockPins.Changed -= OnPinsChanged;
            if (App.Fullscreen != null) App.Fullscreen.Changed -= UpdateFullscreen;
        };
        Loaded += (_, _) => UpdateFullscreen();
        SourceInitialized += (_, _) => Native.MakeNonActivating(new WindowInteropHelper(this).Handle);
    }

    bool IncludeWindow(object item)
    {
        if (item is not ApplicationWindow window || !window.ShowInTaskbar) return false;
        // A pinned app's windows live on its pin.
        if (DockPins.IsPinned(window)) return false;
        // With a dock on every monitor, each dock lists the windows on its own monitor.
        return !App.Settings.DockOnAllMonitors || window.HMonitor == Screen.HMonitor;
    }

    void OnPinsChanged()
    {
        _windows?.Refresh();
        UpdatePinDivider();
    }

    /// <summary>The line between pins and windows only shows when there's something on both sides.</summary>
    void UpdatePinDivider() =>
        PinDivider.Visibility = DockPins.Items.Count > 0 && _windows is { IsEmpty: false } ? Visibility.Visible : Visibility.Collapsed;

    void ShellButton_Click(object sender, RoutedEventArgs e) => App.Instance.ToggleShell();

    // ---------- pinned items ----------

    static DockPinItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as DockPinItem;

    void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) DockPins.Activate(item);
    }

    void Pin_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } || ItemOf(sender) is not { } item)
        {
            e.Handled = true;
            return;
        }

        PinMenu.Fill(menu, item);
    }

    // ---------- drag files, folders or shortcuts onto the dock to pin them ----------

    void Pill_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    void Pill_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
            DockPins.PinPaths(paths);
        e.Handled = true;
    }

    // ---------- fullscreen apps ----------

    // Hidden while a fullscreen or borderless window fills this monitor, even when another
    // monitor has focus (see FullscreenWatcher).
    void UpdateFullscreen() =>
        Visibility = App.Fullscreen?.IsCovered(Screen.HMonitor) == true ? Visibility.Hidden : Visibility.Visible;
}
