using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace DeskShell.Services;

/// <summary>A program that starts when you sign in, from the registry's Run keys or a Startup folder.</summary>
public sealed record StartupApp(string Name, string Command, string? Path, string Source, bool Enabled, bool ForAllUsers,
    RegistryHive Hive, string ApprovedKey, string ValueName);

/// <summary>
/// The same switches as Windows' Task Manager: a program stays in its Run key or Startup folder, and
/// Explorer's "StartupApproved" list says whether it may start. Entries for all users live under
/// HKEY_LOCAL_MACHINE, so changing those goes through Windows' administrator prompt.
/// </summary>
internal static class StartupApps
{
    const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static List<StartupApp> Read()
    {
        var list = new List<StartupApp>();
        ReadRun(list, RegistryHive.CurrentUser, RegistryView.Default, "Run", "Run", false);
        ReadRun(list, RegistryHive.LocalMachine, RegistryView.Registry64, "Run", "Run", true);
        ReadRun(list, RegistryHive.LocalMachine, RegistryView.Registry32, "Run", "Run32", true);
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), RegistryHive.CurrentUser, false);
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), RegistryHive.LocalMachine, true);
        return list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static void ReadRun(List<StartupApp> list, RegistryHive hive, RegistryView view, string runKey, string approvedKey, bool allUsers)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var run = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\" + runKey);
            if (run == null) return;
            foreach (var name in run.GetValueNames())
            {
                if (string.IsNullOrEmpty(name) || run.GetValue(name) is not string command || command.Length == 0) continue;
                list.Add(new StartupApp(name, command, ExeOf(command), allUsers ? "Registry (all users)" : "Registry",
                    IsEnabled(hive, approvedKey, name), allUsers, hive, approvedKey, name));
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't read startup programs from {hive} {runKey}", ex);
        }
    }

    static void ReadFolder(List<StartupApp> list, string folder, RegistryHive hive, bool allUsers)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var fileName = System.IO.Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new StartupApp(System.IO.Path.GetFileNameWithoutExtension(file), file, file,
                    allUsers ? "Startup folder (all users)" : "Startup folder",
                    IsEnabled(hive, "StartupFolder", fileName), allUsers, hive, "StartupFolder", fileName));
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't read the startup folder {folder}", ex);
        }
    }

    /// <summary>No entry, or an even first byte, means it may start.</summary>
    static bool IsEnabled(RegistryHive hive, string approvedKey, string valueName)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(Approved + approvedKey);
            return key?.GetValue(valueName) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Turns a startup program on or off. Returns false if it didn't change (e.g. "No" at the prompt).</summary>
    public static async Task<bool> SetEnabled(StartupApp app, bool enabled)
    {
        // Disabled entries also record when they were turned off, as Windows does.
        var data = new byte[12];
        data[0] = (byte)(enabled ? 2 : 3);
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);

        if (!app.ForAllUsers)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(Approved + app.ApprovedKey);
                key.SetValue(app.ValueName, data, RegistryValueKind.Binary);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"Couldn't change startup program {app.Name}", ex);
                return false;
            }
        }

        // HKEY_LOCAL_MACHINE: reg.exe as administrator
        var hex = Convert.ToHexString(data);
        var args = $"add \"HKLM\\{Approved}{app.ApprovedKey}\" /v \"{app.ValueName.Replace("\"", "\\\"")}\" /t REG_BINARY /d {hex} /f /reg:64";
        try
        {
            using var process = Process.Start(new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "reg.exe"), args)
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process == null) return false;
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false; // "No" at the prompt
        }
        catch (Exception ex)
        {
            Log.Error($"Couldn't change startup program {app.Name}", ex);
            return false;
        }
    }

    /// <summary>The program a command line starts: "C:\x\app.exe" --flags → C:\x\app.exe.</summary>
    public static string? ExeOf(string command)
    {
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        string candidate;
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            candidate = end > 0 ? command[1..end] : command.Trim('"');
        }
        else
        {
            var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            candidate = exe > 0 ? command[..(exe + 4)] : command.Split(' ')[0];
        }
        try { return File.Exists(candidate) ? candidate : null; }
        catch { return null; }
    }
}
