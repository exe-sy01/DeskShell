using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Controls;

namespace DeskShell;

/// <summary>
/// Terminals (normal and as administrator) and common system tools, like Windows' Win+X menu.
/// Administrator entries go through Windows' usual permission prompt.
/// </summary>
public static class ToolsMenu
{
    static string System32(string file) => Path.Combine(Environment.SystemDirectory, file);

    /// <summary>Adds the tools to a menu. <paramref name="beforeRun"/> runs first, e.g. to close the shell.</summary>
    public static void Fill(ItemsControl menu, Action? beforeRun = null)
    {
        void Add(string header, string file, bool admin = false, string? args = null)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) =>
            {
                beforeRun?.Invoke();
                Run(file, admin, args);
            };
            menu.Items.Add(item);
        }

        var terminal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\wt.exe");
        if (File.Exists(terminal))
        {
            Add("Terminal", terminal);
            Add("Terminal (admin)", terminal, admin: true);
        }
        Add("Command Prompt", System32("cmd.exe"));
        Add("Command Prompt (admin)", System32("cmd.exe"), admin: true);
        var powerShell = NewestPowerShell();
        Add("PowerShell", powerShell);
        Add("PowerShell (admin)", powerShell, admin: true);

        menu.Items.Add(new Separator());
        var taskManager = new MenuItem { Header = "Task Manager" };
        taskManager.Click += (_, _) =>
        {
            beforeRun?.Invoke();
            App.Instance.OpenTaskManager();
        };
        menu.Items.Add(taskManager);
        Add("Services", System32("services.msc"));
        Add("Device Manager", System32("devmgmt.msc"));
        Add("Disk Management", System32("diskmgmt.msc"));
        Add("Event Viewer", System32("eventvwr.msc"));
        Add("Network Connections", System32("ncpa.cpl"));
        Add("Registry Editor", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "regedit.exe"));
        Add("Run…", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), args: "shell:::{2559a1f3-21d7-11d4-bdaf-00c04f60b9f0}");
    }

    /// <summary>PowerShell 7 if it's installed, otherwise the one built into Windows.</summary>
    static string NewestPowerShell()
    {
        var places = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"PowerShell\7"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps")
        }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        foreach (var place in places)
        {
            try
            {
                var seven = Path.Combine(place, "pwsh.exe");
                if (File.Exists(seven)) return seven;
            }
            catch (ArgumentException)
            {
                // a malformed PATH entry
            }
        }
        return System32(@"WindowsPowerShell\v1.0\powershell.exe");
    }

    /// <summary>Starts something, as administrator if asked. Saying no to Windows' prompt isn't an error.</summary>
    public static void Run(string file, bool admin = false, string? args = null)
    {
        try
        {
            var start = new ProcessStartInfo(file) { UseShellExecute = true };
            if (args != null) start.Arguments = args;
            if (admin) start.Verb = "runas";
            Process.Start(start);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: "No" at the permission prompt
        }
        catch (Exception ex)
        {
            Log.Error($"Could not start {file}", ex);
        }
    }
}
