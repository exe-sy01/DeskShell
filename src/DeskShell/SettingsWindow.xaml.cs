using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using DeskShell.WindowManagement;

namespace DeskShell;

/// <summary>Settings UI. Edits a copy of the settings and applies it on Save.</summary>
public partial class SettingsWindow : Window
{
    readonly Settings _draft = App.Settings.Clone();

    public SettingsWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            // Title bar follows Windows' light/dark mode, like the rest of the window.
            var on = ThemeManager.IsDark ? 1 : 0;
            WmNative.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, sizeof(int));
        };
        Load();
    }

    void Load()
    {
        StartWithWindows.IsChecked = _draft.StartWithWindows;
        ShellHotkey.Text = _draft.ShellHotkey;
        OverviewHotkey.Text = _draft.OverviewHotkey;
        QuitHotkey.Text = _draft.QuitHotkey;
        GameModeOn.IsChecked = _draft.GameMode;
        GameModeDnd.IsChecked = _draft.GameModeDoNotDisturb;
        ShowIsland.IsChecked = _draft.ShowIsland;
        IslandOnAllMonitors.IsChecked = _draft.IslandOnAllMonitors;
        IslandReserveSpace.IsChecked = _draft.IslandReserveSpace;
        ShowDock.IsChecked = _draft.ShowDock;
        DockOnAllMonitors.IsChecked = _draft.DockOnAllMonitors;
        DockHeight.Text = _draft.DockHeight.ToString(CultureInfo.InvariantCulture);
        WeatherCity.Text = _draft.WeatherCity;
        UpdateRepo.Text = _draft.UpdateRepo;
        ShowInstallState();
        Fahrenheit.IsChecked = _draft.Fahrenheit;
        CustomAltTab.IsChecked = _draft.CustomAltTab;
        RestoreWindowPositions.IsChecked = _draft.RestoreWindowPositions;
        ActiveWindowBorder.IsChecked = _draft.ActiveWindowBorder;
        BorderColor.Text = _draft.BorderColor;
        SnapZones.IsChecked = _draft.SnapZones;
        ZoneGap.Text = _draft.ZoneGap.ToString(CultureInfo.InvariantCulture);
        BuildMonitorLayouts();
        BuildRules();
    }

    // ---------- per-monitor zone layouts ----------

    void BuildMonitorLayouts()
    {
        MonitorLayouts.Children.Clear();
        var screens = ZoneManager.ScreensLeftToRight();
        for (var i = 0; i < screens.Count; i++)
        {
            var screen = screens[i];
            MonitorLayouts.Children.Add(new TextBlock
            {
                Text = $"Monitor {i + 1}{(screen.Primary ? " (main)" : "")}  ·  {screen.Bounds.Width}×{screen.Bounds.Height}",
                Margin = new Thickness(0, 10, 0, 6)
            });
            var current = _draft.ZoneLayouts.TryGetValue(screen.DeviceName, out var id) ? id : ZoneManager.Layouts[0].Id;
            var row = new WrapPanel();
            var group = new List<ToggleButton>();
            foreach (var layout in ZoneManager.Layouts)
            {
                var chip = new ToggleButton
                {
                    Style = (Style)FindResource("ChipButton"),
                    Content = layout.Name,
                    IsChecked = layout.Id == current,
                    Margin = new Thickness(0, 0, 6, 6)
                };
                var deviceName = screen.DeviceName;
                var layoutId = layout.Id;
                chip.Click += (_, _) =>
                {
                    _draft.ZoneLayouts[deviceName] = layoutId;
                    foreach (var other in group) other.IsChecked = other == chip;
                };
                group.Add(chip);
                row.Children.Add(chip);
            }
            MonitorLayouts.Children.Add(row);
        }
    }

    // ---------- app rules ----------

    void BuildRules()
    {
        var count = ZoneManager.ScreensLeftToRight().Count;
        RulesNote.Text = $"When an app opens, put it on a monitor (1 = leftmost, you have {count}) and optionally in a zone of that monitor's layout. " +
                         "App is the program name without .exe, as shown in Task Manager's Details tab (e.g. discord, spotify, chrome). Monitor 0 means \"wherever it opens\".";
        Rules.Children.Clear();
        foreach (var rule in _draft.AppRules) Rules.Children.Add(RuleRow(rule));
        if (_draft.AppRules.Count == 0)
            Rules.Children.Add(new TextBlock { Text = "No rules yet.", Foreground = (Brush)FindResource("TextDimBrush"), Margin = new Thickness(0, 0, 0, 4) });
    }

    UIElement RuleRow(AppRule rule)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        double[] widths = [130, 130, 60, 60, 0, 0, 40];
        foreach (var w in widths)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w == 0 ? GridLength.Auto : new GridLength(w) });

        TextBox Field(string text, string tip, Action<string> set, int col)
        {
            var box = new TextBox { Style = (Style)FindResource("Field"), Text = text, ToolTip = tip, Margin = new Thickness(0, 0, 8, 0) };
            box.TextChanged += (_, _) => set(box.Text);
            Grid.SetColumn(box, col);
            grid.Children.Add(box);
            return box;
        }

        Field(rule.App, "App (program name, e.g. discord)", v => rule.App = v.Trim(), 0);
        Field(rule.TitleContains, "Only when the window title contains… (optional)", v => rule.TitleContains = v, 1);
        Field(rule.Monitor.ToString(CultureInfo.InvariantCulture), "Monitor (1 = leftmost, 0 = any)", v => rule.Monitor = int.TryParse(v, out var n) ? n : 0, 2);
        Field(rule.Zone.ToString(CultureInfo.InvariantCulture), "Zone number (0 = none)", v => rule.Zone = int.TryParse(v, out var n) ? n : 0, 3);

        var max = new CheckBox { Style = (Style)FindResource("Switch"), Content = "Max", IsChecked = rule.Maximize, ToolTip = "Maximise", Margin = new Thickness(0, 0, 10, 0) };
        max.Click += (_, _) => rule.Maximize = max.IsChecked == true;
        Grid.SetColumn(max, 4);
        grid.Children.Add(max);

        var top = new CheckBox { Style = (Style)FindResource("Switch"), Content = "On top", IsChecked = rule.AlwaysOnTop, ToolTip = "Always on top", Margin = new Thickness(0, 0, 10, 0) };
        top.Click += (_, _) => rule.AlwaysOnTop = top.IsChecked == true;
        Grid.SetColumn(top, 5);
        grid.Children.Add(top);

        var remove = new Button
        {
            Style = (Style)FindResource("GhostButton"),
            ToolTip = "Remove rule",
            Content = new TextBlock { Text = "", Style = (Style)FindResource("Icon"), FontSize = 13 }
        };
        remove.Click += (_, _) =>
        {
            _draft.AppRules.Remove(rule);
            BuildRules();
        };
        Grid.SetColumn(remove, 6);
        grid.Children.Add(remove);
        return grid;
    }

    void AddRule_Click(object sender, RoutedEventArgs e)
    {
        _draft.AppRules.Add(new AppRule { Monitor = 1 });
        BuildRules();
    }

    // ---------- buttons ----------

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var problems = new List<string>();

        string Hotkey(TextBox box, string name)
        {
            var text = box.Text.Trim();
            if (!Settings.TryParseHotkey(text, out _, out _)) problems.Add($"{name} hotkey isn't valid");
            return text;
        }

        _draft.ShellHotkey = Hotkey(ShellHotkey, "Shell");
        _draft.OverviewHotkey = Hotkey(OverviewHotkey, "Show-all-windows");
        _draft.QuitHotkey = Hotkey(QuitHotkey, "Quit");
        _draft.StartWithWindows = StartWithWindows.IsChecked == true;
        _draft.GameMode = GameModeOn.IsChecked == true;
        _draft.GameModeDoNotDisturb = GameModeDnd.IsChecked == true;
        _draft.ShowIsland = ShowIsland.IsChecked == true;
        _draft.IslandOnAllMonitors = IslandOnAllMonitors.IsChecked == true;
        _draft.IslandReserveSpace = IslandReserveSpace.IsChecked == true;
        _draft.ShowDock = ShowDock.IsChecked == true;
        _draft.DockOnAllMonitors = DockOnAllMonitors.IsChecked == true;
        if (double.TryParse(DockHeight.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) && h is >= 40 and <= 120)
            _draft.DockHeight = h;
        else problems.Add("dock space must be 40–120");
        _draft.WeatherCity = WeatherCity.Text.Trim();
        _draft.UpdateRepo = UpdateRepo.Text.Trim();
        _draft.Fahrenheit = Fahrenheit.IsChecked == true;
        _draft.CustomAltTab = CustomAltTab.IsChecked == true;
        _draft.RestoreWindowPositions = RestoreWindowPositions.IsChecked == true;
        _draft.ActiveWindowBorder = ActiveWindowBorder.IsChecked == true;
        try
        {
            ColorConverter.ConvertFromString(BorderColor.Text.Trim());
            _draft.BorderColor = BorderColor.Text.Trim();
        }
        catch
        {
            problems.Add("border colour should look like #3FB8C2");
        }
        _draft.SnapZones = SnapZones.IsChecked == true;
        if (int.TryParse(ZoneGap.Text, out var gap) && gap is >= 0 and <= 64) _draft.ZoneGap = gap;
        else problems.Add("gap must be 0–64");
        _draft.AppRules.RemoveAll(r => string.IsNullOrWhiteSpace(r.App));

        if (problems.Count > 0)
        {
            Status.Text = "Not saved: " + string.Join("; ", problems);
            return;
        }

        // Tiles and pins are edited in the shell itself; keep whatever they are now.
        _draft.Tiles = App.Settings.Tiles;
        _draft.PinnedApps = App.Settings.PinnedApps;
        _draft.DockPins = App.Settings.DockPins;
        App.Instance.ApplySettings(_draft.Clone());
        Status.Text = "Saved";
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void ShowInstallState()
    {
        InstallNote.Text = Installer.IsInstalledCopy
            ? $"DeskShell {Installer.VersionText}, installed in {Installer.InstallDir}. Remove it from Settings → Apps → Installed apps."
            : $"DeskShell {Installer.VersionText}, running from {AppContext.BaseDirectory.TrimEnd('\\')}. Installing copies it to {Installer.InstallDir} and adds it to the Start menu and Installed apps.";
        InstallButton.Visibility = Installer.IsInstalledCopy ? Visibility.Collapsed : Visibility.Visible;
        InstallButton.Content = Installer.IsInstalled ? "Install this version over the installed one" : "Install DeskShell";
    }

    void Install_Click(object sender, RoutedEventArgs e)
    {
        // The installer is this exe with --install; it asks this running copy to quit first.
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--install") { UseShellExecute = false });
    }

    async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        // Checks the repo typed here, even before Save.
        var saved = App.Settings.UpdateRepo;
        App.Settings.UpdateRepo = UpdateRepo.Text.Trim();
        UpdateStatus.Text = "Checking…";
        UpdateStatus.Text = await Services.UpdateService.CheckAsync();
        App.Settings.UpdateRepo = saved;
        if (Services.UpdateService.Available != null) UpdateStatus.Text += ". Open the shell to install it.";
    }

    void ResetTiles_Click(object sender, RoutedEventArgs e)
    {
        App.Instance.ResetTiles();
        _draft.Tiles = Settings.DefaultTiles();
        Status.Text = "Tiles reset";
    }

    void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Settings.FilePath) { UseShellExecute = true });
        }
        catch
        {
            Process.Start("notepad.exe", $"\"{Settings.FilePath}\"");
        }
    }
}
