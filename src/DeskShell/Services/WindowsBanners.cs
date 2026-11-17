using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace DeskShell.Services;

/// <summary>
/// While the notch shows notifications, Windows' own pop-up banners are switched off for every
/// app: the same "Show notification banners" switch as in Settings → System → Notifications (each
/// app's ShowBanner value). Notifications still go to the notification centre, where the notch
/// reads them. Each app's previous setting is saved and put back when DeskShell quits or the
/// switch is turned off, so Windows behaves as before without DeskShell.
/// </summary>
public static class WindowsBanners
{
    const string Root = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    const string Value = "ShowBanner";
    static readonly string BackupPath = Path.Combine(Settings.Folder, "banners-backup.json");

    /// <summary>An app's setting before DeskShell changed it: whether Windows had a key for it, and its ShowBanner value (null: none).</summary>
    public sealed record Before(bool KeyExisted, int? Value);

    /// <summary>App id → how it was before DeskShell changed it.</summary>
    static Dictionary<string, Before> _saved = new();
    static bool _active;

    /// <summary>Banners off for every app Windows knows has notified. Call again when new apps turn up.</summary>
    public static async Task HideAsync()
    {
        var known = await NotificationReader.ReadAppIdsAsync();
        try
        {
            LoadBackup();
            using var root = Registry.CurrentUser.CreateSubKey(Root);
            var apps = root.GetSubKeyNames().Concat(known).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var changed = 0;
            foreach (var app in apps)
            {
                // Ids with "\" (like Windows PowerShell's) are nested keys, the same way Windows stores them.
                bool existed;
                using (var existing = root.OpenSubKey(app)) existed = existing != null;
                using var key = root.CreateSubKey(app);
                var now = key.GetValue(Value) as int?;
                if (now == 0) continue;
                if (!_saved.ContainsKey(app)) _saved[app] = new Before(existed, now);
                key.SetValue(Value, 0, RegistryValueKind.DWord);
                changed++;
            }
            _active = true;
            SaveBackup();
            if (changed > 0) Log.Info($"Windows' notification banners off for {changed} app(s); the notch shows them");
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't switch off Windows' notification banners", ex);
        }
    }

    /// <summary>Puts every app's banner setting back as it was.</summary>
    public static void Restore()
    {
        try
        {
            LoadBackup();
            if (_saved.Count == 0) return;
            using var root = Registry.CurrentUser.OpenSubKey(Root, writable: true);
            if (root != null)
                foreach (var (app, before) in _saved)
                {
                    bool empty;
                    using (var key = root.OpenSubKey(app, writable: true))
                    {
                        if (key == null) continue;
                        if (before.Value == null) key.DeleteValue(Value, throwOnMissingValue: false);
                        else key.SetValue(Value, before.Value.Value, RegistryValueKind.DWord);
                        empty = key.ValueCount == 0 && key.SubKeyCount == 0;
                    }
                    // A key DeskShell made goes again, unless Windows has since put something in it.
                    if (!before.KeyExisted && empty) DeleteUpwards(root, app);
                }
            Log.Info($"Windows' notification banners back as they were for {_saved.Count} app(s)");
            _saved.Clear();
            _active = false;
            File.Delete(BackupPath);
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't put Windows' notification banners back", ex);
        }
    }

    public static bool Active => _active;

    /// <summary>Deletes an empty key, then its parents for a nested id, while they're empty too.</summary>
    static void DeleteUpwards(RegistryKey root, string app)
    {
        var path = app;
        while (path.Length > 0)
        {
            using (var key = root.OpenSubKey(path))
                if (key == null || key.ValueCount > 0 || key.SubKeyCount > 0) return;
            root.DeleteSubKey(path, throwOnMissingSubKey: false);
            var cut = path.LastIndexOf('\\');
            if (cut < 0) return;
            path = path[..cut];
        }
    }

    static void LoadBackup()
    {
        if (_saved.Count > 0 || !File.Exists(BackupPath)) return;
        try
        {
            // Left over if DeskShell didn't quit cleanly: these are still the original settings.
            _saved = JsonSerializer.Deserialize<Dictionary<string, Before>>(File.ReadAllText(BackupPath)) ?? new();
        }
        catch
        {
            _saved = new();
        }
    }

    static void SaveBackup()
    {
        Directory.CreateDirectory(Settings.Folder);
        File.WriteAllText(BackupPath, JsonSerializer.Serialize(_saved));
    }
}
