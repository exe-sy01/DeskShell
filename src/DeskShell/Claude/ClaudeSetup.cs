using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeskShell.Claude;

/// <summary>
/// Adds DeskShell's hooks to Claude Code's user settings (%USERPROFILE%\.claude\settings.json), or
/// takes them out again, touching nothing else in the file. The file is copied first, next to
/// itself, so it can always be put back by hand.
/// </summary>
public static class ClaudeSetup
{
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    /// <summary>The events DeskShell listens to. All but the permission request run in the background, so Claude never waits for them.</summary>
    static readonly (string Event, bool Matcher, bool Async, int Timeout)[] Events =
    [
        ("SessionStart", false, true, 10),
        ("UserPromptSubmit", false, true, 10),
        ("PreToolUse", true, true, 10),
        ("PostToolUse", true, true, 10),
        ("PermissionRequest", true, false, ClaudeHook.ApprovalWaitSeconds + 15),
        ("Notification", false, true, 10),
        ("Stop", false, true, 10),
        ("SessionEnd", false, true, 10)
    ];

    const string Marker = "--claude-hook";

    public static bool IsInstalled()
    {
        try
        {
            return File.Exists(SettingsPath) && File.ReadAllText(SettingsPath).Contains(Marker, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Adds the hooks (replacing older DeskShell ones). Returns the backup's path, or throws with why it couldn't.</summary>
    public static string Install(string exe)
    {
        var root = Load();
        var backup = Backup();
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;
        RemoveOurs(hooks);
        foreach (var (name, matcher, background, timeout) in Events)
        {
            var list = hooks[name] as JsonArray ?? new JsonArray();
            hooks[name] = list;
            var handler = new JsonObject
            {
                ["type"] = "command",
                ["command"] = $"\"{exe}\" {Marker} {name}",
                ["timeout"] = timeout
            };
            if (background) handler["async"] = true;
            var group = new JsonObject { ["hooks"] = new JsonArray(handler) };
            if (matcher) group["matcher"] = "*";
            list.Add(group);
        }
        Save(root);
        Log.Info($"Claude Code hooks added (backup: {backup})");
        return backup;
    }

    /// <summary>Takes DeskShell's hooks out, leaving any others.</summary>
    public static void Uninstall()
    {
        if (!File.Exists(SettingsPath)) return;
        var root = Load();
        if (root["hooks"] is not JsonObject hooks) return;
        Backup();
        RemoveOurs(hooks);
        if (hooks.Count == 0) root.Remove("hooks");
        Save(root);
        Log.Info("Claude Code hooks removed");
    }

    static void RemoveOurs(JsonObject hooks)
    {
        foreach (var name in hooks.Select(p => p.Key).ToList())
        {
            if (hooks[name] is not JsonArray groups) continue;
            foreach (var group in groups.OfType<JsonObject>().ToList())
            {
                if (group["hooks"] is not JsonArray handlers) continue;
                foreach (var h in handlers.OfType<JsonObject>().Where(h => h["command"]?.GetValue<string>().Contains(Marker) == true).ToList())
                    handlers.Remove(h);
                if (handlers.Count == 0) groups.Remove(group);
            }
            if (groups.Count == 0) hooks.Remove(name);
        }
    }

    static JsonObject Load()
    {
        if (!File.Exists(SettingsPath)) return new JsonObject();
        var text = File.ReadAllText(SettingsPath);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
               ?? throw new InvalidDataException("Claude Code's settings.json isn't a JSON object");
    }

    static string Backup()
    {
        if (!File.Exists(SettingsPath)) return "(no file yet)";
        var backup = SettingsPath + $".deskshell-backup-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Copy(SettingsPath, backup, overwrite: false);
        return backup;
    }

    static void Save(JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temp = SettingsPath + ".deskshell-new";
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, SettingsPath, overwrite: true);
    }
}
