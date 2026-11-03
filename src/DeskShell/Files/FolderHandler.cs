using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace DeskShell.Files;

/// <summary>
/// Makes Windows open folders (from the desktop, Fences, other apps' "show folder") in DeskShell's
/// explorer, the way the Files app does: a per-user "open" command for folders. Whatever was there
/// before (Files', or nothing for Windows' own) is saved and put back when this is turned off.
/// </summary>
public static class FolderHandler
{
    const string Root = @"Software\Classes\Folder\shell\";
    static readonly string[] Verbs = { "open", "explore" };
    static readonly string BackupPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskShell", "folder-handler-backup.json");

    sealed record Saved(string Verb, bool KeyExisted, string? Command, bool HadDelegate, string? Delegate);

    static string Command(string exe) => $"\"{exe}\" --open \"%1\"";

    public static bool IsOurs()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Root + @"open\command");
        return key?.GetValue(null) is string command && command.Contains("DeskShell.exe\" --open", StringComparison.OrdinalIgnoreCase);
    }

    public static void Apply(bool on, string exe)
    {
        try
        {
            if (on) Take(exe);
            else Give();
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't change which app opens folders", ex);
        }
    }

    static void Take(string exe)
    {
        if (!IsOurs())
        {
            var saved = new List<Saved>();
            foreach (var verb in Verbs)
            {
                using var key = Registry.CurrentUser.OpenSubKey(Root + verb + @"\command");
                saved.Add(new Saved(verb, key != null, key?.GetValue(null, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
                    key?.GetValueNames().Contains("DelegateExecute") == true, key?.GetValue("DelegateExecute") as string));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
            File.WriteAllText(BackupPath, JsonSerializer.Serialize(saved));
        }
        foreach (var verb in Verbs)
        {
            using var key = Registry.CurrentUser.CreateSubKey(Root + verb + @"\command");
            key.SetValue(null, Command(exe), RegistryValueKind.String);
            // An empty DelegateExecute makes Windows run the command instead of Explorer's own handler.
            key.SetValue("DelegateExecute", "", RegistryValueKind.String);
        }
        Log.Info("Folders now open in DeskShell's file explorer");
    }

    static void Give()
    {
        if (!IsOurs()) return;
        List<Saved>? saved = null;
        try
        {
            if (File.Exists(BackupPath)) saved = JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(BackupPath));
        }
        catch
        {
            // unreadable backup: fall back to Windows' own
        }
        foreach (var verb in Verbs)
        {
            var before = saved?.FirstOrDefault(s => s.Verb == verb);
            if (before is { KeyExisted: true })
            {
                using var key = Registry.CurrentUser.CreateSubKey(Root + verb + @"\command");
                if (before.Command != null) key.SetValue(null, before.Command, RegistryValueKind.ExpandString);
                else key.DeleteValue("", false);
                if (before.HadDelegate) key.SetValue("DelegateExecute", before.Delegate ?? "", RegistryValueKind.String);
                else key.DeleteValue("DelegateExecute", false);
            }
            else
            {
                // Nothing was there: Windows' own Explorer takes over again.
                Registry.CurrentUser.DeleteSubKeyTree(Root + verb, throwOnMissingSubKey: false);
            }
        }
        try { File.Delete(BackupPath); } catch { /* fine */ }
        Log.Info("Folders open in the previous app again");
    }
}
