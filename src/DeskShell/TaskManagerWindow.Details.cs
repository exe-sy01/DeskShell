using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskShell.Tasks;

namespace DeskShell;

/// <summary>
/// The panel beside the process list: everything about the selected process (who started it, its
/// command line, memory, priority) and what you can do to it beyond ending it: end its whole
/// tree, suspend and resume it, change its priority.
/// </summary>
public partial class TaskManagerWindow
{
    int _detailsFor = -1;
    CancellationTokenSource? _detailsLoad;

    /// <summary>The process the panel and the extra actions act on: a process row, or an app with just one.</summary>
    static TaskRow? SingleProcess(TaskRow? row) => row switch
    {
        { Kind: TaskRowKind.Process } => row,
        { Kind: TaskRowKind.Group, Children.Count: 1 } => row.Children[0],
        _ => null
    };

    async void ShowDetails()
    {
        var row = Selected;
        if (row == null || row.Kind == TaskRowKind.Section)
        {
            DetailsColumn.Width = new GridLength(0);
            _detailsFor = -1;
            return;
        }
        DetailsColumn.Width = new GridLength(330);
        var process = SingleProcess(row);
        if (process == null)
        {
            // An app with several processes: what they add up to
            _detailsFor = -1;
            DetailsPanel.Children.Clear();
            Heading(row.Name, row.Icon);
            Fact("Processes", row.Children.Count.ToString());
            Fact("CPU", row.CpuText);
            Fact("Memory", row.MemoryText);
            Fact("Process IDs", string.Join(", ", row.Children.Select(c => c.Pid).Take(30)));
            if (row.Path != null) Fact("Location", row.Path, selectable: true);
            Actions(row, null);
            return;
        }
        if (_detailsFor == process.Pid) return; // already showing; refreshed by Refresh button
        _detailsFor = process.Pid;
        _detailsLoad?.Cancel();
        var cancel = _detailsLoad = new CancellationTokenSource();
        DetailsPanel.Children.Clear();
        Heading(row.Name, row.Icon);
        Fact("Process ID", process.Pid.ToString());
        var details = await Task.Run(() => ProcessTools.Details(process.Pid));
        if (cancel.IsCancellationRequested || _closed) return;
        DetailsPanel.Children.Clear();
        Heading(details?.Description ?? row.Name, row.Icon);
        if (details == null)
        {
            Fact("Process ID", process.Pid.ToString());
            Note("This process has ended.");
            return;
        }
        if (details.Company != null) Fact("Publisher", details.Company + (details.Version != null ? $" · {details.Version}" : ""));
        Fact("Process ID", details.Pid + (details.ParentPid is int parent ? $"  ·  started by {parent}" : ""));
        if (details.User != null) Fact("User", details.User + (details.Elevated == true ? "  ·  administrator" : ""));
        if (details.Started is DateTime started) Fact("Started", started.ToString("g") + $"  ({Since(started)})");
        if (details.Priority is { } priority) Fact("Priority", PriorityName(priority));
        if (details.PrivateBytes is long priv) Fact("Private memory", FormatBytes(priv));
        if (details.WorkingSet is long ws) Fact("Working set", FormatBytes(ws) + (details.PeakWorkingSet is long peak ? $"  ·  peak {FormatBytes(peak)}" : ""));
        if (details.Threads is int threads) Fact("Threads · handles", $"{threads:N0} · {details.Handles?.ToString("N0") ?? "—"}");
        if (details.Is32Bit == true) Fact("Architecture", "32-bit");
        if (ProcessTools.IsSuspended(details.Pid)) Fact("State", "Suspended");
        if (details.Path != null) Fact("Location", details.Path, selectable: true);
        if (details.CommandLine != null) Fact("Command line", details.CommandLine, selectable: true);
        Actions(row, details.Pid);
    }

    void Heading(string name, ImageSource? icon)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        if (icon != null) row.Children.Add(new Image { Source = icon, Width = 32, Height = 32, Margin = new Thickness(0, 0, 12, 0) });
        var text = new TextBlock { Text = name, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 240 };
        text.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        row.Children.Add(text);
        DetailsPanel.Children.Add(row);
    }

    void Fact(string label, string value, bool selectable = false)
    {
        var head = new TextBlock { Text = label, FontSize = 11.5, Margin = new Thickness(0, 0, 0, 2) };
        head.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        DetailsPanel.Children.Add(head);
        if (selectable)
        {
            var box = new TextBox { Text = value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                Padding = new Thickness(0), FontSize = 12.5, Margin = new Thickness(-2, 0, 0, 10), MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            box.SetResourceReference(TextBox.ForegroundProperty, "TextBrush");
            box.SetResourceReference(TextBox.CaretBrushProperty, "TextBrush");
            DetailsPanel.Children.Add(box);
            return;
        }
        DetailsPanel.Children.Add(new TextBlock { Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
    }

    void Note(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");
        DetailsPanel.Children.Add(block);
    }

    void Actions(TaskRow row, int? pid)
    {
        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        Button Pill(string text, string glyph, Action click)
        {
            var b = new Button { Style = (Style)FindResource("PillButton"), Tag = glyph, Content = text, Margin = new Thickness(0, 0, 8, 8) };
            b.Click += (_, _) => click();
            buttons.Children.Add(b);
            return b;
        }
        if (row.Window != IntPtr.Zero) Pill("Switch to", "", () => SwitchTo(row));
        if (pid is int p)
        {
            Pill("End tree", "", () => EndTree(p));
            var suspended = ProcessTools.IsSuspended(p);
            Pill(suspended ? "Resume" : "Suspend", suspended ? "" : "", () => { if (suspended) ProcessTools.Resume(p); else ProcessTools.Suspend(p); RefreshDetails(); });
            var priority = Pill("Priority", "", () => { });
            priority.Click += (_, _) =>
            {
                var menu = PriorityMenu(p);
                menu.PlacementTarget = priority;
                menu.IsOpen = true;
            };
        }
        if (row.Path != null)
        {
            var path = row.Path;
            Pill("Open location", "", () => App.Instance.OpenFolder(Path.GetDirectoryName(path), select: path));
        }
        Pill("Refresh", "", RefreshDetails);
        DetailsPanel.Children.Add(buttons);
    }

    void RefreshDetails()
    {
        _detailsFor = -1;
        ShowDetails();
    }

    ContextMenu PriorityMenu(int pid)
    {
        var menu = new ContextMenu();
        var current = ProcessTools.GetPriority(pid);
        foreach (var level in new[] { ProcessPriorityClass.RealTime, ProcessPriorityClass.High, ProcessPriorityClass.AboveNormal, ProcessPriorityClass.Normal, ProcessPriorityClass.BelowNormal, ProcessPriorityClass.Idle })
        {
            var item = new MenuItem { Header = PriorityName(level), IsCheckable = true, IsChecked = current == level };
            item.Click += (_, _) =>
            {
                if (!ProcessTools.SetPriority(pid, level)) Status.Text = "Windows didn't allow changing that process's priority (try running DeskShell as administrator)";
                RefreshDetails();
            };
            menu.Items.Add(item);
        }
        return menu;
    }

    static string PriorityName(ProcessPriorityClass p) => p switch
    {
        ProcessPriorityClass.RealTime => "Realtime",
        ProcessPriorityClass.High => "High",
        ProcessPriorityClass.AboveNormal => "Above normal",
        ProcessPriorityClass.BelowNormal => "Below normal",
        ProcessPriorityClass.Idle => "Low",
        _ => "Normal"
    };

    static string Since(DateTime started)
    {
        var span = DateTime.Now - started;
        return span.TotalMinutes < 1 ? "just now" : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago" : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago" : $"{(int)span.TotalDays} days ago";
    }

    /// <summary>Ends a process and everything it started; ones Windows won't let us end go through the administrator prompt.</summary>
    async void EndTree(int pid)
    {
        if (pid == _ownPid)
        {
            App.Instance.Quit();
            return;
        }
        var all = _last?.List.Select(p => (p.Pid, p.ParentPid)).ToList() ?? new();
        var left = await Task.Run(() => ProcessTools.KillTree(pid, all));
        if (left.Count > 0) await ProcessTools.KillAsAdministratorAsync(left, tree: false);
        _ = TickAsync();
    }

    /// <summary>The list's right-click menu gets the same extra actions.</summary>
    void AddProcessActions(ContextMenu menu, TaskRow row)
    {
        if (SingleProcess(row) is not { } process) return;
        var pid = process.Pid;
        menu.Items.Add(new Separator());
        var tree = new MenuItem { Header = "End process tree" };
        tree.Click += (_, _) => EndTree(pid);
        menu.Items.Add(tree);
        var suspended = ProcessTools.IsSuspended(pid);
        var suspend = new MenuItem { Header = suspended ? "Resume" : "Suspend" };
        suspend.Click += (_, _) => { if (suspended) ProcessTools.Resume(pid); else ProcessTools.Suspend(pid); RefreshDetails(); };
        menu.Items.Add(suspend);
        var priority = new MenuItem { Header = "Set priority" };
        foreach (var item in PriorityMenu(pid).Items.Cast<MenuItem>().ToList())
        {
            ((ContextMenu)item.Parent).Items.Remove(item);
            priority.Items.Add(item);
        }
        menu.Items.Add(priority);
        var copy = new MenuItem { Header = "Copy command line" };
        copy.Click += (_, _) =>
        {
            var line = ProcessTools.CommandLine(pid);
            if (line == null) return;
            try { Clipboard.SetText(line); } catch { /* clipboard busy */ }
        };
        menu.Items.Add(copy);
    }
}
