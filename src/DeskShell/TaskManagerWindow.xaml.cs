using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskShell.Services;
using DeskShell.WindowManagement;
using ManagedShell.WindowsTasks;

namespace DeskShell;

/// <summary>
/// DeskShell's Task Manager: apps and processes with their CPU, memory, I/O and GPU use, the
/// machine's performance over the last minute, and the programs that start with Windows.
/// It only measures anything while it's open (and not minimised).
/// </summary>
public partial class TaskManagerWindow : Window
{
    public static readonly GridLength CpuWidth = new(84), MemoryWidth = new(104), IoWidth = new(96), GpuWidth = new(76);

    readonly ProcessSampler _sampler = new();
    readonly DispatcherTimer _timer;
    readonly ObservableCollection<TaskRow> _rows = new();
    readonly Dictionary<string, TaskRow> _groups = new();
    readonly Dictionary<(int, long), TaskRow> _processes = new();
    readonly Dictionary<string, TaskRow> _sections = new();
    readonly HashSet<string> _expanded = new();
    readonly List<Meter> _meters;
    readonly int _ownPid = Environment.ProcessId;
    readonly SolidColorBrush[] _heat = new SolidColorBrush[11];
    MachineSample? _last;
    bool _sampling, _closed, _startupLoaded;
    string _sort = "Name";
    bool _descending;
    TaskRow? _armed;
    DispatcherTimer? _armTimer;

    public TaskManagerWindow()
    {
        ThemeManager.Use(Resources);
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var dark = 1;
            WmNative.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // dark title bar
            uint black = 0;
            WmNative.DwmSetWindowAttribute(hwnd, 35, ref black, sizeof(uint)); // caption colour, Windows 11
        };

        var accent = TryFindResource("AccentColor") is Color c ? c : Color.FromRgb(0x60, 0xCD, 0xFF);
        for (var i = 0; i < _heat.Length; i++)
        {
            _heat[i] = new SolidColorBrush(Color.FromArgb((byte)(i * 0x0B), accent.R, accent.G, accent.B));
            _heat[i].Freeze();
        }

        Processes.ItemsSource = _rows;
        _meters = new()
        {
            new Meter("CPU", "#4CC2FF", 100),
            new Meter("Memory", "#C084FC", 100),
            new Meter("Disk", "#6CCB5F", 100),
            new Meter("Network", "#F7A325", 0),
            new Meter("GPU", "#FF7A9A", 100)
        };
        Meters.ItemsSource = _meters;
        Meters.SelectedIndex = 0;
        UpdateHeaders();

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => _ = TickAsync(), Dispatcher);
        Loaded += (_, _) =>
        {
            _ = TickAsync();
            Processes.Focus();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            _armTimer?.Stop();
            _sampler.Dispose();
            ThemeManager.Release(Resources);
        };
    }

    // ---------- sampling ----------

    async Task TickAsync()
    {
        if (_sampling || _closed || WindowState == WindowState.Minimized) return;
        _sampling = true;
        try
        {
            var sample = await Task.Run(_sampler.Sample);
            if (_closed) return;
            var windows = WindowsByProcess();
            var info = await Task.Run(() => Describe(sample.List));
            if (_closed) return;
            _last = sample;
            UpdateMeters(sample);
            if (ProcessesPage.Visibility == Visibility.Visible) UpdateProcesses(sample, windows, info);
            UpdateStatus();
        }
        catch (Exception ex)
        {
            Log.Error("Task Manager couldn't update", ex);
        }
        finally
        {
            _sampling = false;
            if (!_closed && !_timer.IsEnabled) _timer.Start();
        }
    }

    /// <summary>Each process's first taskbar window, so apps can be told apart from background processes.</summary>
    static Dictionary<int, IntPtr> WindowsByProcess()
    {
        var result = new Dictionary<int, IntPtr>();
        var windows = App.Instance.Shell?.Tasks?.GroupedWindows?.OfType<ApplicationWindow>().ToList() ?? new();
        foreach (var w in windows)
        {
            if (!w.ShowInTaskbar) continue;
            GetWindowThreadProcessId(w.Handle, out var pid);
            result.TryAdd((int)pid, w.Handle);
        }
        return result;
    }

    // ---------- names and icons ----------

    sealed record FileInfo(string? Description, string? Company, ImageSource? Icon);

    static readonly Dictionary<string, FileInfo> Files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Description and icon of each program file, looked up once (on a background thread).</summary>
    static Dictionary<string, FileInfo> Describe(IEnumerable<ProcessSample> list)
    {
        lock (Files)
        {
            foreach (var path in list.Select(p => p.Path).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
                if (!Files.ContainsKey(path)) Files[path] = Read(path);
            return new Dictionary<string, FileInfo>(Files, StringComparer.OrdinalIgnoreCase);
        }
    }

    static FileInfo Read(string path)
    {
        string? description = null, company = null;
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            description = string.IsNullOrWhiteSpace(version.FileDescription) ? null : version.FileDescription.Trim();
            company = string.IsNullOrWhiteSpace(version.CompanyName) ? null : version.CompanyName.Trim();
        }
        catch
        {
            // unreadable file
        }
        return new FileInfo(description, company, IconOf(path));
    }

    static ImageSource? IconOf(string path)
    {
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

    // ---------- processes ----------

    void UpdateProcesses(MachineSample sample, Dictionary<int, IntPtr> windows, Dictionary<string, FileInfo> files)
    {
        var windowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\";
        var search = SearchBox.Text.Trim();
        var seenProcesses = new HashSet<(int, long)>();
        var members = new Dictionary<string, List<TaskRow>>();

        foreach (var p in sample.List)
        {
            var key = (p.Pid, p.Started);
            seenProcesses.Add(key);
            if (!_processes.TryGetValue(key, out var row))
                _processes[key] = row = new TaskRow(TaskRowKind.Process, $"{p.Pid}:{p.Started}") { Pid = p.Pid, Path = p.Path };
            var file = p.Path != null && files.TryGetValue(p.Path, out var f) ? f : null;
            row.Name = p.Name;
            row.Detail = p.Pid.ToString();
            row.Icon = file?.Icon;
            row.Window = windows.TryGetValue(p.Pid, out var hwnd) ? hwnd : IntPtr.Zero;
            row.IsWindows = p.Path == null || p.Path.StartsWith(windowsFolder, StringComparison.OrdinalIgnoreCase);
            SetValues(row, p.Cpu, p.Memory, p.Io, p.Gpu);

            var groupKey = (p.Path ?? p.Name).ToLowerInvariant();
            if (!members.TryGetValue(groupKey, out var list)) members[groupKey] = list = new();
            list.Add(row);

            if (!_groups.TryGetValue(groupKey, out var group))
                _groups[groupKey] = group = new TaskRow(TaskRowKind.Group, groupKey) { Path = p.Path };
            group.Name = file?.Description ?? Path.GetFileNameWithoutExtension(p.Name);
            group.Icon = file?.Icon;
        }
        foreach (var gone in _processes.Keys.Where(k => !seenProcesses.Contains(k)).ToList()) _processes.Remove(gone);
        foreach (var gone in _groups.Keys.Where(k => !members.ContainsKey(k)).ToList()) _groups.Remove(gone);

        // Groups add up their processes and go in a section
        var sections = new Dictionary<string, List<TaskRow>> { ["Apps"] = new(), ["Background processes"] = new(), ["Windows processes"] = new() };
        foreach (var (key, list) in members)
        {
            var group = _groups[key];
            group.Children = list;
            group.Window = list.Select(r => r.Window).FirstOrDefault(h => h != IntPtr.Zero);
            group.IsWindows = list.All(r => r.IsWindows);
            group.Detail = list.Count > 1 ? $"({list.Count})" : "";
            group.Expanded = list.Count > 1 && _expanded.Contains(key);
            group.Chevron = group.Expanded ? "" : "";
            group.ChevronVisibility = list.Count > 1 ? Visibility.Visible : Visibility.Hidden;
            SetValues(group, list.Sum(r => r.Cpu), list.Sum(r => r.Memory), list.Sum(r => r.Io), Math.Min(100, list.Sum(r => r.Gpu)));
            foreach (var child in list) child.Parent = group;

            var section = group.Window != IntPtr.Zero ? "Apps" : group.IsWindows ? "Windows processes" : "Background processes";
            sections[section].Add(group);
        }

        var desired = new List<TaskRow>();
        foreach (var (name, groups) in sections)
        {
            var shown = groups.Where(g => Matches(g, search)).ToList();
            if (shown.Count == 0) continue;
            if (!_sections.TryGetValue(name, out var heading))
                _sections[name] = heading = new TaskRow(TaskRowKind.Section, name) { Name = name, Weight = FontWeights.SemiBold, IconVisibility = Visibility.Collapsed };
            heading.Detail = $"({shown.Count})";
            desired.Add(heading);
            foreach (var group in Sorted(shown))
            {
                desired.Add(group);
                if (!group.Expanded) continue;
                var nameMatches = search.Length == 0 || group.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase);
                desired.AddRange(Sorted(group.Children.Where(c => nameMatches || Matches(c, search))));
            }
        }
        Sync(desired);
    }

    static bool Matches(TaskRow row, string search)
    {
        if (search.Length == 0) return true;
        if (row.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)) return true;
        if (row.Kind == TaskRowKind.Process) return row.Pid.ToString() == search;
        return row.Children.Any(c => Matches(c, search));
    }

    IEnumerable<TaskRow> Sorted(IEnumerable<TaskRow> rows) => _sort switch
    {
        "Cpu" => _descending ? rows.OrderByDescending(r => r.Cpu) : rows.OrderBy(r => r.Cpu),
        "Memory" => _descending ? rows.OrderByDescending(r => r.Memory) : rows.OrderBy(r => r.Memory),
        "Io" => _descending ? rows.OrderByDescending(r => r.Io) : rows.OrderBy(r => r.Io),
        "Gpu" => _descending ? rows.OrderByDescending(r => r.Gpu) : rows.OrderBy(r => r.Gpu),
        _ => _descending ? rows.OrderByDescending(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                         : rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
    };

    void SetValues(TaskRow row, double cpu, long memory, double io, double gpu)
    {
        row.Cpu = cpu;
        row.Memory = memory;
        row.Io = io;
        row.Gpu = gpu;
        row.CpuText = $"{cpu:0.0}%";
        row.MemoryText = FormatBytes(memory);
        row.IoText = io < 50_000 ? (io < 1 ? "0 MB/s" : "0.1 MB/s") : $"{io / 1_000_000:0.0} MB/s";
        row.GpuText = $"{gpu:0.0}%";
        row.CpuHeat = Heat(cpu / 50);
        row.MemoryHeat = Heat(memory / 2_000_000_000.0);
        row.IoHeat = Heat(io / 20_000_000);
        row.GpuHeat = Heat(gpu / 50);
    }

    Brush Heat(double level) => _heat[(int)Math.Round(Math.Clamp(level, 0, 1) * (_heat.Length - 1))];

    /// <summary>Moves the list to the new order without replacing rows, so selection and scrolling stay put.</summary>
    void Sync(List<TaskRow> desired)
    {
        var wanted = desired.ToHashSet();
        for (var i = _rows.Count - 1; i >= 0; i--)
            if (!wanted.Contains(_rows[i])) _rows.RemoveAt(i);
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < _rows.Count && ReferenceEquals(_rows[i], desired[i])) continue;
            var at = _rows.IndexOf(desired[i]);
            if (at >= 0) _rows.Move(at, i);
            else _rows.Insert(i, desired[i]);
        }
    }

    void UpdateHeaders()
    {
        Button[] headers = { NameHeader, CpuHeader, MemoryHeader, IoHeader, GpuHeader };
        string[] labels = { "Name", "CPU", "Memory", "I/O", "GPU" };
        string[] keys = { "Name", "Cpu", "Memory", "Io", "Gpu" };
        string?[] totals =
        {
            null,
            _last == null ? "" : $"{_last.Cpu:0}%",
            _last == null ? "" : $"{100.0 * _last.MemoryUsed / Math.Max(1, _last.MemoryTotal):0}%",
            _last == null ? "" : $"{_last.DiskActive:0}%",
            _last?.Gpu is double g ? $"{g:0}%" : ""
        };
        for (var i = 0; i < headers.Length; i++)
        {
            var arrow = _sort == keys[i] ? (_descending ? " ↓" : " ↑") : "";
            var panel = new StackPanel();
            if (totals[i] != null) panel.Children.Add(new TextBlock { Text = totals[i], FontSize = 15, Foreground = (Brush)FindResource("TextBrush"), HorizontalAlignment = HorizontalAlignment.Right });
            panel.Children.Add(new TextBlock { Text = labels[i] + arrow, HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom });
            if (i == 0) panel.VerticalAlignment = VerticalAlignment.Bottom;
            headers[i].Content = panel;
        }
        IoHeader.ToolTip = "Reads and writes per second: disk, network and devices. The total above is the disk's active time.";
    }

    void UpdateStatus()
    {
        if (_last == null) return;
        UpdateHeaders();
        if (StartupTab.IsChecked == true) return; // shows its own
        Status.Text = $"{_last.Processes} processes  ·  CPU {_last.Cpu:0}%  ·  Memory {100.0 * _last.MemoryUsed / _last.MemoryTotal:0}% "
                    + $"({FormatBytes(_last.MemoryUsed)} of {FormatBytes(_last.MemoryTotal)})";
    }

    static string FormatBytes(double bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1_073_741_824:0.0} GB",
        >= 1_000_000 => $"{bytes / 1_048_576:0.0} MB",
        _ => $"{bytes / 1024:0} KB"
    };

    static string FormatRate(double bytesPerSecond, bool bits = false)
    {
        if (!bits) return $"{FormatBytes(bytesPerSecond)}/s";
        var b = bytesPerSecond * 8;
        return b >= 1e9 ? $"{b / 1e9:0.0} Gbps" : b >= 1e6 ? $"{b / 1e6:0.0} Mbps" : $"{b / 1e3:0} Kbps";
    }

    // ---------- performance ----------

    void UpdateMeters(MachineSample s)
    {
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        _meters[0].Push(s.Cpu, $"{s.Cpu:0}%", new()
        {
            ["Utilisation"] = $"{s.Cpu:0}%",
            ["Processes"] = s.Processes.ToString("N0"),
            ["Threads"] = s.Threads.ToString("N0"),
            ["Handles"] = s.Handles.ToString("N0"),
            ["Up time"] = $"{(int)uptime.TotalDays}:{uptime:hh\\:mm\\:ss}",
            ["Logical processors"] = Environment.ProcessorCount.ToString()
        });
        var memory = 100.0 * s.MemoryUsed / Math.Max(1, s.MemoryTotal);
        _meters[1].Push(memory, $"{FormatBytes(s.MemoryUsed)} / {FormatBytes(s.MemoryTotal)} ({memory:0}%)", new()
        {
            ["In use"] = FormatBytes(s.MemoryUsed),
            ["Available"] = FormatBytes(s.MemoryTotal - s.MemoryUsed),
            ["Committed"] = $"{FormatBytes(s.Committed)} / {FormatBytes(s.CommitLimit)}",
            ["Installed"] = FormatBytes(s.MemoryTotal)
        });
        _meters[2].Push(s.DiskActive, $"{s.DiskActive:0}%", new()
        {
            ["Active time"] = $"{s.DiskActive:0}%",
            ["Read"] = FormatRate(s.DiskRead),
            ["Write"] = FormatRate(s.DiskWrite)
        });
        _meters[3].Push(s.NetDown + s.NetUp, $"↓ {FormatRate(s.NetDown, bits: true)}  ↑ {FormatRate(s.NetUp, bits: true)}", new()
        {
            ["Receive"] = FormatRate(s.NetDown, bits: true),
            ["Send"] = FormatRate(s.NetUp, bits: true)
        });
        _meters[4].Push(s.Gpu ?? 0, s.Gpu is double g ? $"{g:0}%" : "Not available", new()
        {
            ["Utilisation"] = s.Gpu is double u ? $"{u:0}%" : "Not available"
        });
        ShowMeter();
    }

    void ShowMeter()
    {
        if (Meters.SelectedItem is not Meter meter) return;
        MeterTitle.Text = meter.Name;
        MeterNow.Text = meter.Name switch
        {
            "CPU" => CpuName,
            "Memory" => _last != null ? $"{FormatBytes(_last.MemoryTotal)}" : "",
            _ => ""
        };
        BigGraph.Stroke = meter.Brush;
        BigGraph.Max = meter.GraphMax;
        BigGraph.Values = meter.History;
        BigGraph.Version = meter.Version;
        GraphTop.Text = meter.GraphMax > 0 ? "100%" : FormatRate(meter.History.DefaultIfEmpty(0).Max(), bits: meter.Name == "Network");
        MeterFacts.ItemsSource = meter.Facts;
    }

    static string? _cpuName;
    static string CpuName => _cpuName ??= ReadCpuName();

    static string ReadCpuName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "";
        }
        catch
        {
            return "";
        }
    }

    void Meters_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowMeter();

    // ---------- startup apps ----------

    async Task LoadStartupAsync()
    {
        _startupLoaded = true;
        var rows = await Task.Run(() => StartupApps.Read().Select(app =>
        {
            var file = app.Path != null ? Read(app.Path) : null;
            var who = file?.Company ?? (app.Path != null ? Path.GetFileName(app.Path) : "");
            return new StartupRow(app)
            {
                Icon = file?.Icon,
                Detail = string.Join("  ·  ", new[] { who, app.Source }.Where(s => s.Length > 0)),
                Enabled = app.Enabled
            };
        }).ToList());
        if (_closed) return;
        Startup.ItemsSource = rows;
        if (StartupPage.Visibility == Visibility.Visible)
            Status.Text = $"{rows.Count} startup apps, {rows.Count(r => r.Enabled)} turned on";
    }

    async void StartupSwitch_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StartupRow row) return;
        var want = row.Enabled; // the switch has already flipped it
        var ok = await StartupApps.SetEnabled(row.App, want);
        if (!ok) row.Enabled = !want;
        if (Startup.ItemsSource is List<StartupRow> rows)
            Status.Text = $"{rows.Count} startup apps, {rows.Count(r => r.Enabled)} turned on";
    }

    // ---------- tabs ----------

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (ProcessesPage == null) return; // still loading
        ProcessesPage.Visibility = ProcessesTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PerformancePage.Visibility = PerformanceTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        StartupPage.Visibility = StartupTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.Visibility = EndButton.Visibility = ProcessesTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (StartupTab.IsChecked == true)
        {
            if (!_startupLoaded) _ = LoadStartupAsync();
            else if (Startup.ItemsSource is List<StartupRow> rows) Status.Text = $"{rows.Count} startup apps, {rows.Count(r => r.Enabled)} turned on";
        }
        else
        {
            UpdateStatus();
            if (ProcessesTab.IsChecked == true) _ = TickAsync();
        }
    }

    // ---------- process actions ----------

    TaskRow? Selected => Processes.SelectedItem as TaskRow;

    void Processes_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Disarm();
        EndButton.IsEnabled = Selected is { Kind: not TaskRowKind.Section };
    }

    void Header_Click(object sender, RoutedEventArgs e)
    {
        var key = (string)((Button)sender).Tag;
        if (_sort == key) _descending = !_descending;
        else
        {
            _sort = key;
            _descending = key != "Name"; // biggest first for numbers
        }
        UpdateHeaders();
        _ = TickAsync();
    }

    void Expand_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TaskRow row) Toggle(row);
    }

    void Row_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && (sender as FrameworkElement)?.DataContext is TaskRow { Kind: TaskRowKind.Group } row && row.Children.Count > 1)
        {
            Toggle(row);
            e.Handled = true;
        }
    }

    void Processes_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected is { Kind: TaskRowKind.Group, Children.Count: 1 } single) SwitchTo(single);
        else if (Selected is { Kind: TaskRowKind.Process } process) SwitchTo(process);
    }

    void Toggle(TaskRow group, bool? open = null)
    {
        if (group.Kind != TaskRowKind.Group || group.Children.Count < 2) return;
        if (open ?? !_expanded.Contains(group.Key)) _expanded.Add(group.Key);
        else _expanded.Remove(group.Key);
        if (_last != null) UpdateProcesses(_last, WindowsByProcess(), Describe(Array.Empty<ProcessSample>()));
    }

    static void SwitchTo(TaskRow row)
    {
        if (row.Window != IntPtr.Zero) WmNative.Activate(row.Window);
    }

    void EndTask_Click(object sender, RoutedEventArgs e) => EndSelected();

    /// <summary>Ends the selected app or process. Windows' own processes ask for a second click first.</summary>
    void EndSelected()
    {
        if (Selected is not { Kind: not TaskRowKind.Section } row) return;
        if (row.IsWindows && _armed != row)
        {
            _armed = row;
            EndButton.Content = "Click again: it's part of Windows";
            _armTimer?.Stop();
            _armTimer = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Normal, (_, _) => Disarm(), Dispatcher);
            return;
        }
        Disarm();

        var pids = row.Kind == TaskRowKind.Group ? row.Children.Select(c => c.Pid).ToList() : new List<int> { row.Pid };
        if (pids.Contains(_ownPid))
        {
            App.Instance.Quit(); // ends cleanly, giving the taskbar back
            return;
        }
        var denied = new List<int>();
        foreach (var pid in pids)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill();
            }
            catch (ArgumentException)
            {
                // already gone
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                denied.Add(pid);
            }
        }
        if (denied.Count > 0) EndAsAdministrator(denied);
        _ = TickAsync();
    }

    /// <summary>Processes running as administrator can only be ended as administrator: Windows asks first.</summary>
    static void EndAsAdministrator(List<int> pids)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "taskkill.exe"),
                "/F " + string.Join(" ", pids.Select(p => $"/PID {p}")))
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // "No" at the prompt
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't end a process as administrator", ex);
        }
    }

    void Disarm()
    {
        _armTimer?.Stop();
        _armed = null;
        EndButton.Content = "End task";
    }

    void Processes_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = Processes.ContextMenu!;
        menu.Items.Clear();
        if (Selected is not { Kind: not TaskRowKind.Section } row)
        {
            e.Handled = true;
            return;
        }
        void Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        if (row.Window != IntPtr.Zero) Add("Switch to", () => SwitchTo(row));
        if (row.Kind == TaskRowKind.Group && row.Children.Count > 1) Add(row.Expanded ? "Collapse" : "Expand", () => Toggle(row));
        Add(row.Kind == TaskRowKind.Group && row.Children.Count > 1 ? $"End task ({row.Children.Count} processes)" : "End task", EndSelected);
        if (row.Path != null)
        {
            menu.Items.Add(new Separator());
            var path = row.Path;
            Add("Open file location", () => App.Instance.OpenFolder(Path.GetDirectoryName(path), select: path));
            Add("Properties", () => SHObjectProperties(IntPtr.Zero, 2 /* SHOP_FILEPATH */, path, null));
        }
    }

    // ---------- buttons and keys ----------

    void RunTask_Click(object sender, RoutedEventArgs e) =>
        ToolsMenu.Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), args: "shell:::{2559a1f3-21d7-11d4-bdaf-00c04f60b9f0}");

    void Services_Click(object sender, RoutedEventArgs e) => ToolsMenu.Run(Path.Combine(Environment.SystemDirectory, "services.msc"));

    void WindowsTaskManager_Click(object sender, RoutedEventArgs e) => App.OpenWindowsTaskManager();

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_last != null && IsLoaded) UpdateProcesses(_last, WindowsByProcess(), Describe(Array.Empty<ProcessSample>()));
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && ProcessesTab.IsChecked == true)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocused)
        {
            SearchBox.Text = "";
            Processes.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && ProcessesTab.IsChecked == true && !SearchBox.IsKeyboardFocused)
        {
            EndSelected();
            e.Handled = true;
        }
        else if (e.Key is Key.Right or Key.Left && Selected is { Kind: TaskRowKind.Group } group && !SearchBox.IsKeyboardFocused)
        {
            Toggle(group, e.Key == Key.Right);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Selected != null && !SearchBox.IsKeyboardFocused)
        {
            SwitchTo(Selected);
            e.Handled = true;
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
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool SHObjectProperties(IntPtr hwnd, uint type, string name, string? page);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}

public enum TaskRowKind { Section, Group, Process }

/// <summary>One line of the process list. Rows are kept and updated, so the list doesn't flicker.</summary>
public sealed class TaskRow : INotifyPropertyChanged
{
    public TaskRow(TaskRowKind kind, string key)
    {
        Kind = kind;
        Key = key;
        Indent = kind switch
        {
            TaskRowKind.Section => new Thickness(8, 10, 0, 0),
            TaskRowKind.Group => new Thickness(6, 0, 0, 0),
            _ => new Thickness(58, 0, 0, 0)
        };
        if (kind != TaskRowKind.Group) ChevronVisibility = Visibility.Collapsed;
    }

    public TaskRowKind Kind { get; }
    public string Key { get; }
    public Thickness Indent { get; }
    public int Pid { get; init; }
    public string? Path { get; set; }
    public IntPtr Window { get; set; }
    public bool IsWindows { get; set; }
    public bool Expanded { get; set; }
    public TaskRow? Parent { get; set; }
    public List<TaskRow> Children { get; set; } = new();
    public double Cpu { get; set; }
    public long Memory { get; set; }
    public double Io { get; set; }
    public double Gpu { get; set; }
    public FontWeight Weight { get; init; } = FontWeights.Normal;
    public double NameSize => Kind == TaskRowKind.Section ? 13.5 : 13;
    public Visibility IconVisibility { get; init; } = Visibility.Visible;

    string _name = "", _detail = "", _chevron = "", _cpuText = "", _memoryText = "", _ioText = "", _gpuText = "";
    ImageSource? _icon;
    Visibility _chevronVisibility = Visibility.Visible;
    Brush? _cpuHeat, _memoryHeat, _ioHeat, _gpuHeat;

    public string Name { get => _name; set => Set(ref _name, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public string Chevron { get => _chevron; set => Set(ref _chevron, value); }
    public Visibility ChevronVisibility { get => _chevronVisibility; set => Set(ref _chevronVisibility, value); }
    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }
    public string CpuText { get => _cpuText; set => Set(ref _cpuText, value); }
    public string MemoryText { get => _memoryText; set => Set(ref _memoryText, value); }
    public string IoText { get => _ioText; set => Set(ref _ioText, value); }
    public string GpuText { get => _gpuText; set => Set(ref _gpuText, value); }
    public Brush? CpuHeat { get => _cpuHeat; set => Set(ref _cpuHeat, value); }
    public Brush? MemoryHeat { get => _memoryHeat; set => Set(ref _memoryHeat, value); }
    public Brush? IoHeat { get => _ioHeat; set => Set(ref _ioHeat, value); }
    public Brush? GpuHeat { get => _gpuHeat; set => Set(ref _gpuHeat, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>One of the performance graphs: the last minute of a value.</summary>
public sealed class Meter : INotifyPropertyChanged
{
    public Meter(string name, string colour, double graphMax)
    {
        Name = name;
        GraphMax = graphMax;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colour));
        brush.Freeze();
        Brush = brush;
    }

    public string Name { get; }
    /// <summary>The top of the graph; 0 means "the biggest value shown".</summary>
    public double GraphMax { get; }
    public Brush Brush { get; }
    public List<double> History { get; } = new();
    public Dictionary<string, string> Facts { get; private set; } = new();
    public string Short { get; private set; } = "";
    public int Version { get; private set; }

    public void Push(double value, string text, Dictionary<string, string> facts)
    {
        History.Add(value);
        if (History.Count > 60) History.RemoveAt(0);
        Short = text;
        Facts = facts;
        Version++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Short)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Version)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A startup program, with the switch's state.</summary>
public sealed class StartupRow : INotifyPropertyChanged
{
    public StartupRow(StartupApp app) => App = app;

    public StartupApp App { get; }
    public string Name => App.Name;
    public string Command => App.Command;
    public string Detail { get; init; } = "";
    public ImageSource? Icon { get; init; }

    bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A filled line graph of up to 60 values, newest on the right.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaxProperty = DependencyProperty.Register(nameof(Max), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(nameof(Version), typeof(int), typeof(Sparkline),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty GridProperty = DependencyProperty.Register(nameof(Grid), typeof(bool), typeof(Sparkline),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public double Max { get => (double)GetValue(MaxProperty); set => SetValue(MaxProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public int Version { get => (int)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    public bool Grid { get => (bool)GetValue(GridProperty); set => SetValue(GridProperty, value); }

    static readonly Pen GridPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)), 1));

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (Grid)
        {
            for (var i = 1; i < 4; i++) dc.DrawLine(GridPen, new Point(0, h * i / 4), new Point(w, h * i / 4));
            for (var i = 1; i < 6; i++) dc.DrawLine(GridPen, new Point(w * i / 6, 0), new Point(w * i / 6, h));
        }
        var values = Values;
        if (values == null || values.Count < 2 || w <= 0 || h <= 0) return;

        var max = Max > 0 ? Max : Math.Max(1, values.Max() * 1.15);
        var step = w / 59;
        var x0 = w - step * (values.Count - 1);
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var l = line.Open())
        using (var a = area.Open())
        {
            Point P(int i) => new(x0 + step * i, h - Math.Clamp(values[i] / max, 0, 1) * (h - 2) - 1);
            l.BeginFigure(P(0), false, false);
            a.BeginFigure(new Point(x0, h), true, true);
            a.LineTo(P(0), false, false);
            for (var i = 1; i < values.Count; i++)
            {
                l.LineTo(P(i), true, true);
                a.LineTo(P(i), false, false);
            }
            a.LineTo(new Point(w, h), false, false);
        }
        line.Freeze();
        area.Freeze();
        var fill = Stroke.Clone();
        fill.Opacity = 0.18;
        fill.Freeze();
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(Stroke, 1.5) { LineJoin = PenLineJoin.Round }, line);
    }

    static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }
}
