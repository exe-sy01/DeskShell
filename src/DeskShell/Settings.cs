using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace DeskShell;

public sealed class TileSetting
{
    public string Id { get; set; } = "";
    public int Width { get; set; } = 1;
    public int Height { get; set; } = 1;

    public TileSetting() { }
    public TileSetting(string id, int width, int height) { Id = id; Width = width; Height = height; }
}

public sealed class PinnedApp
{
    public string Name { get; set; } = "";
    /// <summary>The app's AppsFolder id (AUMID or path), launched through shell:AppsFolder.</summary>
    public string Target { get; set; } = "";
}

/// <summary>Something pinned to the dock.</summary>
public sealed class DockPin
{
    public string Name { get; set; } = "";
    /// <summary>"app" = AppsFolder id (launched via shell:AppsFolder); "exe" = program path; "path" = file, folder or shortcut.</summary>
    public string Kind { get; set; } = "app";
    public string Target { get; set; } = "";
}

/// <summary>"When this app opens, put it here."</summary>
public sealed class AppRule
{
    /// <summary>Process name without .exe, e.g. "discord". Case doesn't matter.</summary>
    public string App { get; set; } = "";
    /// <summary>Optional: only windows whose title contains this text.</summary>
    public string TitleContains { get; set; } = "";
    /// <summary>1 = leftmost monitor, 2 = next one to the right… 0 = leave it where it opens.</summary>
    public int Monitor { get; set; }
    /// <summary>Zone number in that monitor's layout (1-based). 0 = no zone.</summary>
    public int Zone { get; set; }
    public bool Maximize { get; set; }
    public bool AlwaysOnTop { get; set; }
}

/// <summary>User settings, stored as editable JSON in %APPDATA%\DeskShell\settings.json.</summary>
public sealed class Settings
{
    // Hotkeys
    public string ShellHotkey { get; set; } = "Alt+Space";
    public string OverviewHotkey { get; set; } = "Ctrl+Alt+Up";
    public string QuitHotkey { get; set; } = "Ctrl+Alt+Shift+Q";

    // Dock
    /// <summary>Off = no dock at all, just the shell (Alt+Space); windows get the whole screen.</summary>
    public bool ShowDock { get; set; } = true;
    public bool DockOnAllMonitors { get; set; } = true;
    public double DockHeight { get; set; } = 64;

    // General
    public bool StartWithWindows { get; set; }
    /// <summary>GitHub repository ("owner/name") whose releases DeskShell updates from. Empty = no update checks.</summary>
    public string UpdateRepo { get; set; } = "";

    // Game mode: while something runs fullscreen
    public bool GameMode { get; set; } = true;
    public bool GameModeDoNotDisturb { get; set; } = true;

    // Weather
    /// <summary>City name for weather. Empty = no weather.</summary>
    public string WeatherCity { get; set; } = "";
    public bool Fahrenheit { get; set; }

    // Shell tiles, in display order. Tiles not listed are hidden.
    public List<TileSetting> Tiles { get; set; } = DefaultTiles();
    public List<PinnedApp> PinnedApps { get; set; } = new();
    /// <summary>Items pinned to the dock, left to right.</summary>
    public List<DockPin> DockPins { get; set; } = new();

    // Window management
    public bool ActiveWindowBorder { get; set; } = true;
    public string BorderColor { get; set; } = "#3FB8C2";
    public bool SnapZones { get; set; } = true;
    public int ZoneGap { get; set; } = 8;
    /// <summary>Monitor device name → zone layout id.</summary>
    public Dictionary<string, string> ZoneLayouts { get; set; } = new();
    public bool CustomAltTab { get; set; } = true;
    public bool RestoreWindowPositions { get; set; } = true;
    public List<AppRule> AppRules { get; set; } = new();

    /// <summary>Tiles this settings file already knows about; tiles added in later versions get placed once.</summary>
    public List<string> KnownTiles { get; set; } = new();

    /// <summary>Tiles added after the first release, with where they go in an existing layout (after which tile).</summary>
    static readonly (string Id, int W, int H, string After)[] NewTiles = [("games", 4, 2, "pinned"), ("mixer", 2, 3, "audioout")];

    public static List<TileSetting> DefaultTiles() => new()
    {
        new("pinned", 4, 1),
        new("games", 4, 2),
        new("wifi", 1, 1),
        new("bluetooth", 1, 1),
        new("calendar", 2, 3),
        new("focus", 1, 1),
        new("darkmode", 1, 1),
        new("volume", 2, 1),
        new("media", 2, 1),
        new("audioout", 2, 1),
        new("mixer", 2, 3),
        new("brightness", 2, 1),
        new("stats", 2, 1),
        new("weather", 1, 1),
        new("layouts", 1, 1),
        new("clipboard", 2, 2),
        new("screenshot", 1, 1),
        new("notifications", 2, 2),
        new("tray", 2, 2),
    };

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskShell");

    public static string FilePath => Path.Combine(Folder, "settings.json");

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions) ?? new Settings();
                loaded.PlaceNewTiles();
                loaded.Save(); // writes any settings added since the file was created
                return loaded;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not read settings.json, using defaults", ex);
            return new Settings();
        }

        var fresh = new Settings();
        fresh.Save();
        return fresh;
    }

    /// <summary>Adds tiles introduced since this layout was saved (once; removing them later sticks).</summary>
    void PlaceNewTiles()
    {
        foreach (var (id, w, h, after) in NewTiles)
        {
            if (KnownTiles.Contains(id)) continue;
            KnownTiles.Add(id);
            if (Tiles.Any(t => t.Id == id)) continue;
            var index = Tiles.FindIndex(t => t.Id == after);
            Tiles.Insert(index < 0 ? 0 : index + 1, new TileSetting(id, w, h));
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error("Could not write settings.json", ex);
        }
    }

    public Settings Clone() => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;

    /// <summary>Parses text like "Ctrl+Alt+Space" into RegisterHotKey modifiers and a virtual key.</summary>
    public static bool TryParseHotkey(string text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= Native.MOD_CONTROL; break;
                case "alt": modifiers |= Native.MOD_ALT; break;
                case "shift": modifiers |= Native.MOD_SHIFT; break;
                case "win" or "windows": modifiers |= Native.MOD_WIN; break;
                default:
                    if (!Enum.TryParse<Key>(raw, true, out var key)) return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        return vk != 0;
    }
}
