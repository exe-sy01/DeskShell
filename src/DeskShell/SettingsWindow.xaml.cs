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
        AppDrawerHotkey.Text = _draft.AppDrawerHotkey;
        WinKeyOpensDrawer.IsChecked = _draft.WinKeyOpensDrawer;
        UseDeskShellTaskManager.IsChecked = _draft.UseDeskShellTaskManager;
        UseDeskShellFiles.IsChecked = _draft.UseDeskShellFiles;
        OpenAllFoldersInDeskShell.IsChecked = _draft.OpenAllFoldersInDeskShell;
        FileSearch.IsChecked = _draft.FileSearch;
        SearchHotkey.Text = _draft.SearchHotkey;
        IslandAppsButton.IsChecked = _draft.Island.AppsButton;
        IslandDiscordMessages.IsChecked = _draft.Island.DiscordMessages;
        QuitHotkey.Text = _draft.QuitHotkey;
        GameModeOn.IsChecked = _draft.GameMode;
        GameModeDnd.IsChecked = _draft.GameModeDoNotDisturb;
        ShowIsland.IsChecked = _draft.ShowIsland;
        UseNotch.IsChecked = _draft.UseNotch;
        HideWindowsBanners.IsChecked = _draft.Notch.HideWindowsBanners;
        IslandOnAllMonitors.IsChecked = _draft.IslandOnAllMonitors;
        IslandReserveSpace.IsChecked = _draft.IslandReserveSpace;
        ShowWindowsTaskbar.IsChecked = _draft.ShowWindowsTaskbar;
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
        BuildTheme();
        ShowClaudeState();
        // Closing without saving puts the saved theme back.
        Closed += (_, _) => { if (!_themeSaved) ThemeManager.Refresh(); };
    }

    // ---------- Claude Code ----------

    void ShowClaudeState()
    {
        var on = Claude.ClaudeSetup.IsInstalled();
        ClaudeConnect.Content = on ? "Disconnect Claude Code" : "Connect Claude Code";
        ClaudeStatus.Text = on ? "Connected" : "Not connected";
    }

    /// <summary>Applies at once (it edits Claude Code's own settings, not DeskShell's).</summary>
    void ClaudeConnect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Claude.ClaudeSetup.IsInstalled())
            {
                Claude.ClaudeSetup.Uninstall();
                ShowClaudeState();
                ClaudeStatus.Text = "Disconnected";
            }
            else
            {
                var backup = Claude.ClaudeSetup.Install(Installer.IsInstalled ? Installer.InstalledExe : Environment.ProcessPath!);
                ShowClaudeState();
                ClaudeStatus.Text = $"Connected (backup: {System.IO.Path.GetFileName(backup)})";
            }
        }
        catch (Exception ex)
        {
            ClaudeStatus.Text = "Couldn't change Claude Code's settings: " + ex.Message;
        }
    }

    // ---------- theme ----------

    bool _themeSaved;
    readonly Dictionary<string, (Border Swatch, TextBox Hex)> _themeRows = new();
    bool _fillingTheme;

    void BuildTheme()
    {
        ThemePresets.Children.Clear();
        var chips = new List<ToggleButton>();
        foreach (var (key, label) in ThemeManager.PresetNames)
        {
            var chip = new ToggleButton { Style = (Style)FindResource("ChipButton"), Content = label, IsChecked = _draft.Theme.Preset == key, Margin = new Thickness(0, 0, 6, 6) };
            // Checked (not Click), so keyboard and screen readers pick presets too.
            chip.Checked += (_, _) =>
            {
                if (_draft.Theme.Preset == key) return;
                _draft.Theme.Preset = key;
                foreach (var other in chips) if (other != chip) other.IsChecked = false;
                ShowThemeColours();
                PreviewTheme();
            };
            chip.Unchecked += (_, _) => { if (_draft.Theme.Preset == key) chip.IsChecked = true; }; // one is always picked
            chips.Add(chip);
            ThemePresets.Children.Add(chip);
        }
        ThemeWindowsAccent.IsChecked = _draft.Theme.WindowsAccent;

        ThemeColours.Children.Clear();
        _themeRows.Clear();
        foreach (var (role, label) in ThemeManager.Roles)
        {
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock { Text = label, Style = (Style)FindResource("Label") };
            var swatch = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Pick a colour" };
            swatch.SetResourceReference(Border.BorderBrushProperty, "StrongEdgeBrush");
            var hex = new TextBox { Style = (Style)FindResource("Field"), ToolTip = "#RRGGBB, or #AARRGGBB for see-through" };
            var reset = new Button { Style = (Style)FindResource("GhostButton"), ToolTip = "Back to the preset's colour", Margin = new Thickness(6, 0, 0, 0),
                Content = new TextBlock { Text = "", Style = (Style)FindResource("Icon"), FontSize = 12 } };
            Grid.SetColumn(swatch, 1);
            Grid.SetColumn(hex, 2);
            Grid.SetColumn(reset, 3);
            grid.Children.Add(name);
            grid.Children.Add(swatch);
            grid.Children.Add(hex);
            grid.Children.Add(reset);
            ThemeColours.Children.Add(grid);
            _themeRows[role] = (swatch, hex);

            var r = role;
            hex.TextChanged += (_, _) =>
            {
                if (_fillingTheme || !ThemeManager.TryParse(hex.Text, out var colour)) return;
                _draft.Theme.Colors[r] = ThemeManager.ToHex(colour);
                if (r == "accent") ThemeWindowsAccent.IsChecked = _draft.Theme.WindowsAccent = false;
                swatch.Background = new SolidColorBrush(colour);
                PreviewTheme();
            };
            reset.Click += (_, _) =>
            {
                _draft.Theme.Colors.Remove(r);
                ShowThemeColours();
                PreviewTheme();
            };
            swatch.MouseLeftButtonUp += (_, _) => PickColour(swatch, hex);
        }
        ShowThemeColours();
    }

    /// <summary>Each colour box shows your own colour, or the preset's.</summary>
    void ShowThemeColours()
    {
        _fillingTheme = true;
        var p = ThemeManager.Resolve(_draft.Theme);
        foreach (var (role, (swatch, hex)) in _themeRows)
        {
            var colour = role switch
            {
                "background" => p.Background, "card" => p.Card, "raised" => p.Raised, "edge" => p.Edge,
                "text" => p.Text, "textDim" => p.TextDim, _ => p.Accent
            };
            hex.Text = ThemeManager.ToHex(colour);
            hex.FontWeight = _draft.Theme.Colors.ContainsKey(role) ? FontWeights.SemiBold : FontWeights.Normal;
            swatch.Background = new SolidColorBrush(colour);
        }
        _fillingTheme = false;
    }

    void PreviewTheme()
    {
        _themeSaved = false;
        ThemeManager.Preview(_draft.Theme);
    }

    void ThemeWindowsAccent_Click(object sender, RoutedEventArgs e)
    {
        _draft.Theme.WindowsAccent = ThemeWindowsAccent.IsChecked == true;
        if (_draft.Theme.WindowsAccent) _draft.Theme.Colors.Remove("accent");
        ShowThemeColours();
        PreviewTheme();
    }

    void ResetTheme_Click(object sender, RoutedEventArgs e)
    {
        _draft.Theme.Colors.Clear();
        ShowThemeColours();
        PreviewTheme();
    }

    /// <summary>A small palette under the swatch: greys and a range of hues in four shades.</summary>
    void PickColour(Border swatch, TextBox hex)
    {
        var grid = new UniformGrid { Columns = 10 };
        var colours = new List<Color>();
        for (var i = 0; i < 10; i++) colours.Add(Color.FromRgb((byte)(i * 255 / 9), (byte)(i * 255 / 9), (byte)(i * 255 / 9)));
        double[] hues = [0, 20, 40, 60, 120, 165, 195, 215, 265, 300];
        foreach (var (s, l) in new[] { (0.9, 0.82), (0.85, 0.65), (0.9, 0.5), (0.75, 0.33), (0.6, 0.18) })
            foreach (var h in hues) colours.Add(FromHsl(h, s, l));
        var popup = new Popup { PlacementTarget = swatch, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
        foreach (var c in colours)
        {
            var dot = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(3), Background = new SolidColorBrush(c), Cursor = System.Windows.Input.Cursors.Hand };
            dot.MouseLeftButtonUp += (_, _) =>
            {
                hex.Text = ThemeManager.ToHex(c);
                popup.IsOpen = false;
            };
            grid.Children.Add(dot);
        }
        var box = new Border { Padding = new Thickness(8), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Child = grid, Margin = new Thickness(0, 6, 0, 0) };
        box.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        box.SetResourceReference(Border.BorderBrushProperty, "EdgeBrush");
        popup.Child = box;
        popup.IsOpen = true;
    }

    static Color FromHsl(double h, double s, double l)
    {
        double C(double n)
        {
            var k = (n + h / 30) % 12;
            var a = s * Math.Min(l, 1 - l);
            return l - a * Math.Max(-1, Math.Min(Math.Min(k - 3, 9 - k), 1));
        }
        return Color.FromRgb((byte)Math.Round(C(0) * 255), (byte)Math.Round(C(8) * 255), (byte)Math.Round(C(4) * 255));
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
        _draft.AppDrawerHotkey = Hotkey(AppDrawerHotkey, "App-drawer");
        _draft.WinKeyOpensDrawer = WinKeyOpensDrawer.IsChecked == true;
        _draft.UseDeskShellTaskManager = UseDeskShellTaskManager.IsChecked == true;
        _draft.UseDeskShellFiles = UseDeskShellFiles.IsChecked == true;
        _draft.OpenAllFoldersInDeskShell = OpenAllFoldersInDeskShell.IsChecked == true;
        _draft.FileSearch = FileSearch.IsChecked == true;
        _draft.SearchHotkey = SearchHotkey.Text.Trim().Length == 0 ? "" : Hotkey(SearchHotkey, "Search-files");
        _draft.Island.AppsButton = IslandAppsButton.IsChecked == true;
        _draft.Island.DiscordMessages = IslandDiscordMessages.IsChecked == true;
        _draft.QuitHotkey = Hotkey(QuitHotkey, "Quit");
        _draft.StartWithWindows = StartWithWindows.IsChecked == true;
        _draft.GameMode = GameModeOn.IsChecked == true;
        _draft.GameModeDoNotDisturb = GameModeDnd.IsChecked == true;
        _draft.ShowIsland = ShowIsland.IsChecked == true;
        _draft.IslandLook = UseNotch.IsChecked == true ? "notch" : "pill";
        _draft.Notch.HideWindowsBanners = HideWindowsBanners.IsChecked == true;
        _draft.IslandOnAllMonitors = IslandOnAllMonitors.IsChecked == true;
        _draft.IslandReserveSpace = IslandReserveSpace.IsChecked == true;
        _draft.ShowWindowsTaskbar = ShowWindowsTaskbar.IsChecked == true;
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
        _draft.RecentApps = App.Settings.RecentApps;
        App.Instance.ApplySettings(_draft.Clone());
        _themeSaved = true;
        Status.Text = "Saved";
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Applies the island design copied from Island Workshop straight away.</summary>
    void PasteIsland_Click(object sender, RoutedEventArgs e)
    {
        string text;
        try { text = Clipboard.GetText(); }
        catch { text = ""; }
        var live = App.Settings;
        var problem = IslandWorkshop.Apply(text, live);
        if (problem != null)
        {
            IslandImportStatus.Text = problem;
            return;
        }
        live.Save();
        App.Instance.RebuildBars("island design pasted");
        // Keep the draft in step so Save doesn't undo it.
        _draft.Island = live.Clone().Island;
        _draft.IslandOnAllMonitors = live.IslandOnAllMonitors;
        _draft.IslandReserveSpace = live.IslandReserveSpace;
        IslandOnAllMonitors.IsChecked = live.IslandOnAllMonitors;
        IslandReserveSpace.IsChecked = live.IslandReserveSpace;
        IslandImportStatus.Text = "Applied";
    }

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
