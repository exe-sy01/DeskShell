using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using ManagedShell;
using ManagedShell.AppBar;
using ManagedShell.WindowsTasks;

namespace DeskShell;

/// <summary>
/// The slim window dock at the bottom of a monitor. It reserves screen space like the
/// Windows taskbar and hides itself while a fullscreen app (a game) is running.
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

        SourceInitialized += (_, _) => Native.MakeNonActivating(new WindowInteropHelper(this).Handle);
    }

    bool IncludeWindow(object item)
    {
        if (item is not ApplicationWindow window || !window.ShowInTaskbar) return false;
        // With a dock on every monitor, each dock lists the windows on its own monitor.
        return !App.Settings.DockOnAllMonitors || window.HMonitor == Screen.HMonitor;
    }

    void ShellButton_Click(object sender, RoutedEventArgs e) => App.Instance.ToggleShell();

    protected override void OnFullScreenEnter(FullScreenApp app)
    {
        base.OnFullScreenEnter(app);
        Visibility = Visibility.Hidden;
    }

    protected override void OnFullScreenLeave()
    {
        base.OnFullScreenLeave();
        Visibility = Visibility.Visible;
    }
}
