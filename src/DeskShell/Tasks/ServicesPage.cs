using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;

namespace DeskShell.Tasks;

/// <summary>
/// Windows' services: what's running, how each one starts, and buttons to start, stop, restart or
/// change it. Reading needs no special rights; every change does, so changes run sc.exe through
/// Windows' administrator prompt and the list is read again once it's done. Descriptions are only
/// looked up for the service you select, because asking for all of them makes the list slow to appear.
/// </summary>
public sealed class ServicesPage : Grid
{
    readonly ObservableCollection<ServiceRow> _rows = new();
    readonly Dictionary<string, ServiceRow> _byName = new(StringComparer.OrdinalIgnoreCase);
    readonly ListBox _list;
    readonly TextBox _search;
    readonly RadioButton _all, _running, _stopped;
    readonly TextBlock _count, _empty;

    readonly Border _details;
    readonly TextBlock _title, _facts, _description, _path, _message;
    readonly Button _start, _stop, _restart;
    readonly ToggleButton[] _types;

    bool _loading, _again, _loaded, _hasData, _busy;

    /// <summary>"312 services, 140 running", for the window's status line.</summary>
    public string Summary { get; private set; } = "";
    public event EventHandler? SummaryChanged;

    public ServicesPage()
    {
        TaskPageParts.Base(this);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ---------- toolbar ----------
        _search = TaskPageParts.Search("Search services");
        _search.TextChanged += (_, _) => Show();
        var group = "ServiceFilter" + GetHashCode();
        RadioButton Chip(string text, bool on)
        {
            var chip = new RadioButton { Content = text, GroupName = group, IsChecked = on, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            chip.SetResourceReference(StyleProperty, "ChipButton");
            chip.Checked += (_, _) => Show();
            return chip;
        }
        _all = Chip("All", true);
        _running = Chip("Running", false);
        _stopped = Chip("Stopped", false);
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 0), Children = { _all, _running, _stopped } };

        _count = TaskPageParts.Text("", "TextDimBrush", 12.5);
        _count.VerticalAlignment = VerticalAlignment.Center;
        _count.Margin = new Thickness(14, 0, 8, 0);
        var refresh = TaskPageParts.Ghost("", "Look again");
        refresh.Click += (_, _) => Refresh();
        var openServices = TaskPageParts.Pill("", "Open Services", "Windows' own Services console");
        openServices.Margin = new Thickness(6, 0, 0, 0);
        openServices.Click += (_, _) => ToolsMenu.Run(Path.Combine(Environment.SystemDirectory, "services.msc"));

        var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition());
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(chips, 1);
        Grid.SetColumn(_count, 2);
        Grid.SetColumn(refresh, 3);
        Grid.SetColumn(openServices, 4);
        _count.HorizontalAlignment = HorizontalAlignment.Right;
        toolbar.Children.Add(_search);
        toolbar.Children.Add(chips);
        toolbar.Children.Add(_count);
        toolbar.Children.Add(refresh);
        toolbar.Children.Add(openServices);

        // ---------- column names (lined up with the row template) ----------
        var header = new Grid { Margin = new Thickness(21, 0, 27, 6) };
        foreach (var width in ColumnWidths)
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        string[] labels = ["Name", "Service", "Status", "Start type", "PID"];
        for (var i = 0; i < labels.Length; i++)
        {
            var label = TaskPageParts.Text(labels[i], "TextDimBrush", 12);
            if (i == labels.Length - 1) label.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(label, i);
            header.Children.Add(label);
        }

        // ---------- list ----------
        _list = new ListBox
        {
            ItemsSource = _rows,
            ItemTemplate = RowTemplate.Value,
            ItemContainerStyle = RowContainer.Value,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 6, 0, 6),
            FocusVisualStyle = null
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetCanContentScroll(_list, true);
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        _list.SelectionChanged += (_, _) => ShowDetails();
        _empty = TaskPageParts.Text("Looking…", "TextDimBrush", 13);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Top;
        _empty.Margin = new Thickness(0, 40, 0, 0);
        _empty.IsHitTestVisible = false;
        var listCard = TaskPageParts.Card(new Grid { Children = { _list, _empty } });

        // ---------- details of the selected service ----------
        _title = TaskPageParts.Text("", "TextBrush", 15, FontWeights.SemiBold);
        _facts = TaskPageParts.Text("", "TextDimBrush", 12);
        _facts.Margin = new Thickness(0, 2, 0, 0);
        _description = TaskPageParts.Text("", "TextDimBrush", 12.5);
        _description.TextTrimming = TextTrimming.None;
        _description.TextWrapping = TextWrapping.Wrap;
        _description.MaxHeight = 54; // three lines; the tooltip has the rest
        _description.Margin = new Thickness(0, 8, 0, 0);
        _path = TaskPageParts.Text("", "FaintBrush", 11.5);
        _path.Margin = new Thickness(0, 6, 0, 0);
        _message = TaskPageParts.Text("", "DangerBrush", 12.5);
        _message.TextTrimming = TextTrimming.None;
        _message.TextWrapping = TextWrapping.Wrap;
        _message.Margin = new Thickness(0, 6, 0, 0);
        var about = new StackPanel { Margin = new Thickness(0, 0, 20, 0), Children = { _title, _facts, _description, _path, _message } };

        const string adminTip = "Windows asks for administrator permission";
        _start = TaskPageParts.Pill("", "Start", adminTip);
        _stop = TaskPageParts.Pill("", "Stop", adminTip);
        _restart = TaskPageParts.Pill("", "Restart", adminTip);
        _stop.Margin = _restart.Margin = new Thickness(6, 0, 0, 0);
        _start.Click += (_, _) => { if (Selected is { } row) _ = ChangeAsync(row, "Starting", Sc, $"start \"{row.Name}\""); };
        _stop.Click += (_, _) => { if (Selected is { } row) _ = ChangeAsync(row, "Stopping", Sc, $"stop \"{row.Name}\""); };
        // sc.exe can't wait for a stop before starting again, so a restart is one PowerShell command (still one prompt).
        _restart.Click += (_, _) =>
        {
            if (Selected is { } row)
                _ = ChangeAsync(row, "Restarting", PowerShell,
                    $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"Restart-Service -Name '{row.Name.Replace("'", "''")}' -Force -ErrorAction Stop\"");
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { _start, _stop, _restart } };

        (string Label, string Arg)[] types = [("Automatic", "auto"), ("Delayed", "delayed-auto"), ("Manual", "demand"), ("Disabled", "disabled")];
        _types = new ToggleButton[types.Length];
        var typeRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var typeLabel = TaskPageParts.Text("Start type", "TextDimBrush", 12);
        typeLabel.VerticalAlignment = VerticalAlignment.Center;
        typeLabel.Margin = new Thickness(0, 0, 10, 0);
        typeRow.Children.Add(typeLabel);
        for (var i = 0; i < types.Length; i++)
        {
            var (label, arg) = types[i];
            var chip = new ToggleButton
            {
                Content = label,
                Margin = new Thickness(i == 0 ? 0 : 6, 0, 0, 0),
                ToolTip = (label == "Delayed" ? "Automatic, a little after sign-in. " : "") + adminTip
            };
            chip.SetResourceReference(StyleProperty, "ChipButton");
            chip.Click += (_, _) =>
            {
                if (Selected is { } row) _ = ChangeAsync(row, "Changing how it starts", Sc, $"config \"{row.Name}\" start= {arg}", configChanged: true);
            };
            _types[i] = chip;
            typeRow.Children.Add(chip);
        }
        var controls = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Children = { actions, typeRow } };

        var detailsGrid = new Grid();
        detailsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        detailsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(controls, 1);
        detailsGrid.Children.Add(about);
        detailsGrid.Children.Add(controls);
        _details = TaskPageParts.Card(detailsGrid);
        _details.Padding = new Thickness(18, 14, 18, 14);
        _details.Margin = new Thickness(0, 10, 0, 0);
        _details.Visibility = Visibility.Collapsed;

        Grid.SetRow(header, 1);
        Grid.SetRow(listCard, 2);
        Grid.SetRow(_details, 3);
        Children.Add(toolbar);
        Children.Add(header);
        Children.Add(listCard);
        Children.Add(_details);

        // Fills itself the first time it's shown; after that the window decides when to look again.
        Loaded += (_, _) =>
        {
            if (!_loaded) Refresh();
        };
    }

    static string Sc => Path.Combine(Environment.SystemDirectory, "sc.exe");
    static string PowerShell => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    static readonly GridLength[] ColumnWidths =
        [new GridLength(1, GridUnitType.Star), new GridLength(190), new GridLength(120), new GridLength(170), new GridLength(64)];

    ServiceRow? Selected => _list.SelectedItem as ServiceRow;

    /// <summary>
    /// Reads every service again (on a background thread; takes a few tens of milliseconds, so the
    /// window can call this every few seconds while the page is showing).
    /// </summary>
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
        if (_loading)
        {
            _again = true; // the read in progress may predate a change: read once more after it
            return;
        }
        _loading = true;
        try
        {
            do
            {
                _again = false;
                var services = await Task.Run(ServiceControl.Enumerate);
                Apply(services);
            } while (_again);
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't list services", ex);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Updates the rows in place (so selection and scrolling stay put), adding and removing as needed.</summary>
    void Apply(List<ServiceInfo> services)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in services)
        {
            seen.Add(info.Name);
            if (!_byName.TryGetValue(info.Name, out var row)) _byName[info.Name] = row = new ServiceRow(info.Name);
            row.Update(info);
        }
        foreach (var gone in _byName.Keys.Where(k => !seen.Contains(k)).ToList()) _byName.Remove(gone);
        _hasData = true;
        Show();
    }

    void Show()
    {
        if (_list == null) return; // still building
        var search = _search.Text.Trim();
        bool Has(string s) => s.Contains(search, StringComparison.CurrentCultureIgnoreCase);
        var shown = _byName.Values
            .Where(r => _running.IsChecked != true || r.State == ServiceControl.Running)
            .Where(r => _stopped.IsChecked != true || r.State == ServiceControl.Stopped)
            .Where(r => search.Length == 0 || Has(r.DisplayName) || Has(r.Name) || r.PidText == search)
            .OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        TaskPageParts.Sync(_rows, shown);

        // Until the first read lands, the empty text keeps saying "Looking…".
        if (_hasData) _empty.Text = _byName.Count == 0 ? "No services found." : "No services match.";
        _empty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Summary = $"{_byName.Count} services, {_byName.Values.Count(r => r.State == ServiceControl.Running)} running";
        _count.Text = Summary;
        SummaryChanged?.Invoke(this, EventArgs.Empty);
        ShowDetails();
    }

    // ---------- details ----------

    void ShowDetails()
    {
        if (Selected is not { } row)
        {
            _details.Visibility = Visibility.Collapsed;
            return;
        }
        _details.Visibility = Visibility.Visible;
        _title.Text = row.DisplayName;
        var facts = new List<string> { row.Name, row.StatusText };
        if (row.Pid != 0) facts.Add($"PID {row.Pid}");
        if (row.Details?.Account is { Length: > 0 } account) facts.Add($"Runs as {account}");
        _facts.Text = string.Join("  ·  ", facts);

        if (row.Details is { } details)
        {
            _description.Text = details.Description is { Length: > 0 } d ? d : "No description.";
            _description.ToolTip = details.Description is { Length: > 0 } ? details.Description : null;
            var path = details.Path ?? "";
            if (details.DependsOn.Length > 0) path += (path.Length > 0 ? "  ·  " : "") + "Needs " + string.Join(", ", details.DependsOn);
            _path.Text = path;
            _path.ToolTip = path.Length > 0 ? path : null;
        }
        else
        {
            _description.Text = "";
            _path.Text = "";
            _ = LoadDetailsAsync(row);
        }
        UpdateButtons();
    }

    /// <summary>Description, program and account of one service, looked up once it's selected.</summary>
    async Task LoadDetailsAsync(ServiceRow row)
    {
        if (row.LoadingDetails) return;
        row.LoadingDetails = true;
        try
        {
            var name = row.Name;
            row.Details = await Task.Run(() => ServiceControl.Details(name)) ?? ServiceDetails.Unknown;
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't read service {row.Name}", ex);
            row.Details = ServiceDetails.Unknown;
        }
        finally
        {
            row.LoadingDetails = false;
        }
        if (ReferenceEquals(Selected, row)) ShowDetails();
    }

    void UpdateButtons()
    {
        var row = Selected;
        var can = row != null && !_busy;
        var stoppable = row is { State: ServiceControl.Running } && (row.ControlsAccepted & 1) != 0; // SERVICE_ACCEPT_STOP
        TaskPageParts.Enable(_start, can && row!.State == ServiceControl.Stopped && row.StartType != 4);
        TaskPageParts.Enable(_stop, can && stoppable);
        TaskPageParts.Enable(_restart, can && stoppable);
        var current = row == null ? -1 : row.StartType switch
        {
            2 => row.Delayed ? 1 : 0,
            3 => 2,
            4 => 3,
            _ => -1 // boot/system drivers, or unreadable
        };
        for (var i = 0; i < _types.Length; i++)
        {
            _types[i].IsChecked = i == current;
            TaskPageParts.Enable(_types[i], can && row!.StartType is >= 2 and <= 4);
        }
    }

    void SetMessage(string text, bool error)
    {
        _message.Text = text;
        _message.SetResourceReference(TextBlock.ForegroundProperty, error ? "DangerBrush" : "TextDimBrush");
    }

    /// <summary>
    /// Runs a change as administrator and waits for it, then reads the list again, and keeps looking
    /// while the service is still starting or stopping (up to about ten seconds).
    /// </summary>
    async Task ChangeAsync(ServiceRow row, string doing, string file, string args, bool configChanged = false)
    {
        if (_busy) return;
        _busy = true;
        UpdateButtons();
        SetMessage(doing + "…", error: false);
        try
        {
            var code = await ProcessTools.RunElevatedAsync(file, args);
            if (code == null)
            {
                SetMessage("", error: false); // "No" at the prompt
                return;
            }
            SetMessage(code == 0 ? "" : Explain(code.Value), error: code != 0);
            if (configChanged) row.Details = null;
            await Task.Delay(400);
            await LoadAsync();
            for (var i = 0; i < 14 && row.IsPending; i++)
            {
                await Task.Delay(700);
                await LoadAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't change service {row.Name}", ex);
            SetMessage("Something went wrong.", error: true);
        }
        finally
        {
            _busy = false;
            if (ReferenceEquals(Selected, row)) ShowDetails();
            else UpdateButtons();
        }
    }

    /// <summary>sc.exe exits with the Windows error code; these are the ones worth putting into words.</summary>
    static string Explain(int code) => code switch
    {
        5 => "Windows doesn't allow changing this service, even as administrator.",
        1051 => "Other running services depend on this one. Stop those first.",
        1053 => "The service didn't respond in time.",
        1056 => "It's already running.",
        1058 => "It's disabled. Change its start type first.",
        1060 => "This service no longer exists.",
        1061 => "It can't take commands right now. Try again in a moment.",
        1062 => "It isn't running.",
        1 => "Windows couldn't do that.", // PowerShell's general failure
        _ => $"Windows couldn't do that (error {code})."
    };

    // ---------- templates ----------

    static readonly Lazy<DataTemplate> RowTemplate = new(() => (DataTemplate)XamlReader.Parse("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Grid Height="34">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="190" />
                    <ColumnDefinition Width="120" />
                    <ColumnDefinition Width="170" />
                    <ColumnDefinition Width="64" />
                </Grid.ColumnDefinitions>
                <TextBlock Text="{Binding DisplayName}" FontSize="13" Margin="0,0,12,0" TextTrimming="CharacterEllipsis"
                           VerticalAlignment="Center" Foreground="{DynamicResource TextBrush}" />
                <TextBlock Grid.Column="1" Text="{Binding Name}" FontSize="12.5" Margin="0,0,12,0" TextTrimming="CharacterEllipsis"
                           VerticalAlignment="Center" Foreground="{DynamicResource TextDimBrush}" />
                <StackPanel Grid.Column="2" Orientation="Horizontal" VerticalAlignment="Center">
                    <Ellipse x:Name="Dot" Width="7" Height="7" Margin="0,0,8,0" Fill="{DynamicResource FaintBrush}" />
                    <TextBlock Text="{Binding StatusText}" FontSize="12.5" Foreground="{DynamicResource TextBrush}" />
                </StackPanel>
                <TextBlock Grid.Column="3" Text="{Binding StartTypeText}" FontSize="12.5" TextTrimming="CharacterEllipsis"
                           VerticalAlignment="Center" Foreground="{DynamicResource TextDimBrush}" />
                <TextBlock Grid.Column="4" Text="{Binding PidText}" FontSize="12.5" HorizontalAlignment="Right"
                           VerticalAlignment="Center" Foreground="{DynamicResource TextDimBrush}" />
            </Grid>
            <DataTemplate.Triggers>
                <DataTrigger Binding="{Binding IsPending}" Value="True">
                    <Setter TargetName="Dot" Property="Fill" Value="{DynamicResource TextDimBrush}" />
                </DataTrigger>
                <DataTrigger Binding="{Binding IsRunning}" Value="True">
                    <Setter TargetName="Dot" Property="Fill" Value="{DynamicResource AccentBrush}" />
                </DataTrigger>
            </DataTemplate.Triggers>
        </DataTemplate>
        """));

    static readonly Lazy<Style> RowContainer = new(() => (Style)XamlReader.Parse("""
        <Style TargetType="ListBoxItem" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Setter Property="FocusVisualStyle" Value="{x:Null}" />
            <Setter Property="HorizontalContentAlignment" Value="Stretch" />
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="ListBoxItem">
                        <Grid Margin="6,1">
                            <Border x:Name="Bg" Background="Transparent" CornerRadius="10" Padding="14,0">
                                <ContentPresenter />
                            </Border>
                            <Border x:Name="Pill" Width="3" Height="16" CornerRadius="1.5" HorizontalAlignment="Left"
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
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        """));
}

/// <summary>One line of the services list. Rows are kept and updated, so the list doesn't jump.</summary>
public sealed class ServiceRow : INotifyPropertyChanged
{
    public ServiceRow(string name) => Name = name;

    public string Name { get; }
    public uint State { get; private set; }
    /// <summary>0 boot, 1 system, 2 automatic, 3 manual, 4 disabled; null if Windows wouldn't say.</summary>
    public uint? StartType { get; private set; }
    public bool Delayed { get; private set; }
    public uint ControlsAccepted { get; private set; }
    public int Pid { get; private set; }
    internal ServiceDetails? Details { get; set; }
    internal bool LoadingDetails { get; set; }

    string _displayName = "", _statusText = "", _startTypeText = "", _pidText = "";
    bool _isRunning, _isPending;

    public string DisplayName { get => _displayName; private set => Set(ref _displayName, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string StartTypeText { get => _startTypeText; private set => Set(ref _startTypeText, value); }
    public string PidText { get => _pidText; private set => Set(ref _pidText, value); }
    public bool IsRunning { get => _isRunning; private set => Set(ref _isRunning, value); }
    /// <summary>Starting, stopping, pausing or resuming.</summary>
    public bool IsPending { get => _isPending; private set => Set(ref _isPending, value); }

    internal void Update(ServiceInfo info)
    {
        State = info.State;
        StartType = info.StartType;
        Delayed = info.Delayed;
        ControlsAccepted = info.ControlsAccepted;
        Pid = info.Pid;
        DisplayName = string.IsNullOrWhiteSpace(info.DisplayName) ? info.Name : info.DisplayName;
        StatusText = info.State switch
        {
            1 => "Stopped", 2 => "Starting", 3 => "Stopping", 4 => "Running",
            5 => "Resuming", 6 => "Pausing", 7 => "Paused", _ => ""
        };
        StartTypeText = info.StartType switch
        {
            0 => "Boot", 1 => "System",
            2 => info.Delayed ? "Automatic (delayed)" : "Automatic",
            3 => "Manual", 4 => "Disabled", _ => ""
        };
        PidText = info.Pid != 0 ? info.Pid.ToString() : "";
        IsRunning = info.State == ServiceControl.Running;
        IsPending = info.State is 2 or 3 or 5 or 6;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>What the service list says about one service.</summary>
internal sealed record ServiceInfo(string Name, string DisplayName, uint State, uint ControlsAccepted, int Pid, uint? StartType, bool Delayed);

/// <summary>The slower facts about one service, read only for the selected one.</summary>
internal sealed record ServiceDetails(string? Description, string? Path, string? Account, string[] DependsOn)
{
    public static readonly ServiceDetails Unknown = new(null, null, null, []);
}

/// <summary>
/// Reads services straight from the Service Control Manager. Only read rights are asked for, which
/// every user has for nearly every service; changes go through sc.exe as administrator instead.
/// </summary>
internal static class ServiceControl
{
    public const uint Stopped = 1, Running = 4;

    const uint SC_MANAGER_CONNECT = 0x0001, SC_MANAGER_ENUMERATE_SERVICE = 0x0004, SERVICE_QUERY_CONFIG = 0x0001;
    const uint SERVICE_WIN32 = 0x30, SERVICE_STATE_ALL = 3;
    const int SC_ENUM_PROCESS_INFO = 0, ERROR_MORE_DATA = 234, ERROR_INSUFFICIENT_BUFFER = 122;
    const int SERVICE_CONFIG_DESCRIPTION = 1, SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;

    /// <summary>Every Windows service (not drivers), with its state, process and start type.</summary>
    public static List<ServiceInfo> Enumerate()
    {
        var result = new List<ServiceInfo>();
        var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT | SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero) return result;

        var size = 128 * 1024;
        var buffer = Marshal.AllocHGlobal(size);
        var config = new ConfigBuffer();
        try
        {
            var resume = 0;
            var stride = Marshal.SizeOf<ENUM_SERVICE_STATUS_PROCESS>();
            for (var round = 0; round < 64; round++)
            {
                var ok = EnumServicesStatusEx(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_STATE_ALL, buffer, size,
                    out var needed, out var count, ref resume, null);
                var error = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok && error != ERROR_MORE_DATA) break;

                for (var i = 0; i < count; i++)
                {
                    var entry = Marshal.PtrToStructure<ENUM_SERVICE_STATUS_PROCESS>(buffer + i * stride);
                    var name = Marshal.PtrToStringUni(entry.ServiceName) ?? "";
                    if (name.Length == 0) continue;
                    var display = Readable(Marshal.PtrToStringUni(entry.DisplayName)) ?? name;
                    var (startType, delayed) = StartTypeOf(scm, name, config);
                    result.Add(new ServiceInfo(name, display, entry.Status.CurrentState, entry.Status.ControlsAccepted,
                        (int)entry.Status.ProcessId, startType, delayed));
                }

                if (ok) break;
                if (count == 0 && needed > size)
                {
                    // Not even one more fits: make room for the rest.
                    Marshal.FreeHGlobal(buffer);
                    size = needed;
                    buffer = Marshal.AllocHGlobal(size);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            config.Dispose();
            CloseServiceHandle(scm);
        }
        return result;
    }

    /// <summary>Start type and whether automatic start is delayed. (null, false) if the service won't say.</summary>
    static (uint? StartType, bool Delayed) StartTypeOf(IntPtr scm, string name, ConfigBuffer config)
    {
        var service = OpenService(scm, name, SERVICE_QUERY_CONFIG);
        if (service == IntPtr.Zero) return (null, false);
        try
        {
            if (!config.Read(service)) return (null, false);
            var startType = config.Config.StartType;
            var delayed = false;
            if (startType == 2)
            {
                var flag = Marshal.AllocHGlobal(8);
                try
                {
                    delayed = QueryServiceConfig2(service, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, flag, 8, out _) && Marshal.ReadInt32(flag) != 0;
                }
                finally
                {
                    Marshal.FreeHGlobal(flag);
                }
            }
            return (startType, delayed);
        }
        finally
        {
            CloseServiceHandle(service);
        }
    }

    /// <summary>Description, program, account and dependencies of one service.</summary>
    public static ServiceDetails? Details(string name)
    {
        var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) return null;
        try
        {
            var service = OpenService(scm, name, SERVICE_QUERY_CONFIG);
            if (service == IntPtr.Zero) return null;
            using var config = new ConfigBuffer();
            try
            {
                string? path = null, account = null;
                var dependsOn = Array.Empty<string>();
                if (config.Read(service))
                {
                    path = Marshal.PtrToStringUni(config.Config.BinaryPathName);
                    account = Marshal.PtrToStringUni(config.Config.ServiceStartName);
                    dependsOn = MultiString(config.Config.Dependencies);
                }

                string? description = null;
                QueryServiceConfig2(service, SERVICE_CONFIG_DESCRIPTION, IntPtr.Zero, 0, out var needed);
                if (needed > 0)
                {
                    var buffer = Marshal.AllocHGlobal(needed);
                    try
                    {
                        if (QueryServiceConfig2(service, SERVICE_CONFIG_DESCRIPTION, buffer, needed, out _))
                            description = Readable(Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer)));
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
                return new ServiceDetails(description, string.IsNullOrWhiteSpace(path) ? null : path.Trim(), account, dependsOn);
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    /// <summary>"Dhcp\0+NetworkProvider\0\0" → ["Dhcp", "NetworkProvider"] (a + marks a group).</summary>
    static string[] MultiString(IntPtr p)
    {
        var list = new List<string>();
        if (p == IntPtr.Zero) return [];
        for (var guard = 0; guard < 256; guard++)
        {
            var s = Marshal.PtrToStringUni(p);
            if (string.IsNullOrEmpty(s)) break;
            list.Add(s.TrimStart('+'));
            p += (s.Length + 1) * 2;
        }
        return list.ToArray();
    }

    /// <summary>Most names arrive readable, but some are "@file.dll,-123" references to a translated string.</summary>
    static string? Readable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (!text.StartsWith('@')) return text;
        var output = new StringBuilder(1024);
        return SHLoadIndirectString(text, output, output.Capacity, IntPtr.Zero) == 0 ? output.ToString() : text;
    }

    /// <summary>A reusable buffer for QueryServiceConfig, which wants up to 8 KB per service.</summary>
    sealed class ConfigBuffer : IDisposable
    {
        IntPtr _buffer = Marshal.AllocHGlobal(8192);
        int _size = 8192;
        public QUERY_SERVICE_CONFIG Config;

        public bool Read(IntPtr service)
        {
            if (!QueryServiceConfig(service, _buffer, _size, out var needed))
            {
                if (Marshal.GetLastWin32Error() != ERROR_INSUFFICIENT_BUFFER || needed <= _size) return false;
                Marshal.FreeHGlobal(_buffer);
                _size = needed;
                _buffer = Marshal.AllocHGlobal(_size);
                if (!QueryServiceConfig(service, _buffer, _size, out _)) return false;
            }
            Config = Marshal.PtrToStructure<QUERY_SERVICE_CONFIG>(_buffer);
            return true;
        }

        public void Dispose()
        {
            if (_buffer == IntPtr.Zero) return;
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }

    // ---------- Windows ----------

    [StructLayout(LayoutKind.Sequential)]
    struct SERVICE_STATUS_PROCESS
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ENUM_SERVICE_STATUS_PROCESS
    {
        public IntPtr ServiceName, DisplayName;
        public SERVICE_STATUS_PROCESS Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct QUERY_SERVICE_CONFIG
    {
        public uint ServiceType, StartType, ErrorControl;
        public IntPtr BinaryPathName, LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies, ServiceStartName, DisplayName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenSCManagerW")]
    static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenServiceW")]
    static extern IntPtr OpenService(IntPtr scm, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "EnumServicesStatusExW")]
    static extern bool EnumServicesStatusEx(IntPtr scm, int infoLevel, uint serviceType, uint serviceState, IntPtr services, int size,
        out int needed, out int returned, ref int resumeHandle, string? group);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryServiceConfigW")]
    static extern bool QueryServiceConfig(IntPtr service, IntPtr config, int size, out int needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryServiceConfig2W")]
    static extern bool QueryServiceConfig2(IntPtr service, int infoLevel, IntPtr buffer, int size, out int needed);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int SHLoadIndirectString(string source, StringBuilder output, int size, IntPtr reserved);
}
