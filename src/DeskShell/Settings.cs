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

/// <summary>How the island looks and behaves. Island Workshop (an artifact) designs these; see <see cref="IslandWorkshop"/>.</summary>
public sealed class IslandStyle
{
    public double Width { get; set; } = 420;
    public double Height { get; set; } = 40;
    /// <summary>Corner rounding as a percentage of half the height (100 = a full pill).</summary>
    public double Roundness { get; set; } = 100;
    /// <summary>"black", "glass" (see-through dark) or "tint" (dark with a touch of the accent colour).</summary>
    public string Surface { get; set; } = "black";
    /// <summary>"none", "subtle" or "accent".</summary>
    public string Edge { get; set; } = "subtle";
    /// <summary>"none", "soft" or "strong".</summary>
    public string Shadow { get; set; } = "soft";
    /// <summary>Gap between the top of the screen and the island.</summary>
    public double TopGap { get; set; } = 8;
    public bool HideFullscreen { get; set; } = true;
    /// <summary>A thin bar until the mouse comes near.</summary>
    public bool Peek { get; set; }

    /// <summary>The pages, in order: notifications, music, windows, programs, clock.</summary>
    public List<string> Pages { get; set; } = ["notifications", "music", "windows", "programs"];
    public bool StartOnLastPage { get; set; } = true;
    /// <summary>The ‹ › arrows: "always", "hover" or "hidden".</summary>
    public string Arrows { get; set; } = "always";
    public bool PageDots { get; set; }
    /// <summary>A button on the island that opens the app drawer.</summary>
    public bool AppsButton { get; set; } = true;
    /// <summary>The wheel scrolls a long row of icons before flipping pages.</summary>
    public bool WheelScrollsIconsFirst { get; set; } = true;

    public bool MusicArt { get; set; } = true;
    public bool MusicArtist { get; set; } = true;
    public bool MusicControls { get; set; } = true;
    public bool MusicBars { get; set; }
    public bool NotificationText { get; set; } = true;
    public bool NotificationCount { get; set; } = true;
    /// <summary>Each island lists every monitor's windows (false: only its own monitor's).</summary>
    public bool WindowsFromAllMonitors { get; set; } = true;
    public bool ActiveWindowTitle { get; set; }
    public double IconSize { get; set; } = 18;

    // Discord voice calls (sent by the AIChat Vencord plugin)
    /// <summary>A Voice call page appears while you're in a Discord call.</summary>
    public bool ShowCalls { get; set; } = true;
    /// <summary>Switch to it when you join a call.</summary>
    public bool JumpToCall { get; set; } = true;
    /// <summary>Don't shrink to the peek bar during a call (otherwise the peek bar shows whoever's talking).</summary>
    public bool CallKeepsOpen { get; set; }

    /// <summary>DMs and direct mentions from Discord (sent by the same plugin) count as notifications.</summary>
    public bool DiscordMessages { get; set; } = true;

    // Dynamic moments
    public bool JumpToMusic { get; set; }
    public bool JumpToNotifications { get; set; }
    public bool GrowForNotifications { get; set; }
    public double MomentSeconds { get; set; } = 5;

    // Opening the shell
    /// <summary>"double" (double-click), "single" (click on empty space) or "hover" (rest the mouse on it).</summary>
    public string OpenWith { get; set; } = "double";
    /// <summary>How much the island grows under the mouse, in percent.</summary>
    public double HoverGrow { get; set; } = 2.5;
    public double ExpandMs { get; set; } = 380;
    /// <summary>Overshoot of the expand animation, in percent.</summary>
    public double Bounce { get; set; } = 22;
    /// <summary>How much the screen behind the shell darkens, in percent.</summary>
    public double Dim { get; set; } = 30;
    public double ShellRadius { get; set; } = 28;
    public bool SidePanels { get; set; } = true;

    [JsonIgnore] public double CornerRadius => Height / 2 * Math.Clamp(Roundness, 0, 100) / 100;
    /// <summary>The strip at the top of the screen kept free for the island.</summary>
    [JsonIgnore] public double ReservedHeight => TopGap + Height + 6;
}

/// <summary>The notch: a small clock at the top edge that grows into the Home panel when you rest the mouse on it.</summary>
public sealed class NotchStyle
{
    /// <summary>Colour that breathes along the notch's edges (#RRGGBB).</summary>
    public string GlowColor { get; set; } = "#E040FB";
    public bool Glow { get; set; } = true;
    /// <summary>The glow slowly fades in and out (off: it stays lit).</summary>
    public bool Breathing { get; set; } = true;
    /// <summary>Colour of the gauges, bars and the timer in the panel.</summary>
    public string PanelColor { get; set; } = "#FF6A1F";
    /// <summary>How long the mouse rests on the notch before it opens, in milliseconds.</summary>
    public int HoverDelay { get; set; } = 250;
    public bool Seconds { get; set; }
    /// <summary>Tabs in the bar, in order.</summary>
    public List<string> Tabs { get; set; } = ["home", "windows", "system", "clipboard", "notes", "focus", "files", "apps", "weather", "media", "screentime"];
    public int FocusMinutes { get; set; } = 25;
    public int BreakMinutes { get; set; } = 5;
}

/// <summary>User settings, stored as editable JSON in %APPDATA%\DeskShell\settings.json.</summary>
public sealed class Settings
{
    // Hotkeys
    public string ShellHotkey { get; set; } = "Alt+Space";
    public string OverviewHotkey { get; set; } = "Ctrl+Alt+Up";
    public string QuitHotkey { get; set; } = "Ctrl+Alt+Shift+Q";
    public string AppDrawerHotkey { get; set; } = "Alt+Shift+Space";
    /// <summary>A tap of the Windows key opens the app drawer instead of the Start menu (Win+ shortcuts still work).</summary>
    public bool WinKeyOpensDrawer { get; set; } = true;
    /// <summary>Ctrl+Shift+Esc and the Tools menus open DeskShell's Task Manager instead of Windows'.</summary>
    public bool UseDeskShellTaskManager { get; set; } = true;
    /// <summary>Win+E and DeskShell's folder links open DeskShell's file explorer instead of Windows'.</summary>
    public bool UseDeskShellFiles { get; set; } = true;
    /// <summary>Windows opens every folder (from the desktop, other apps…) in DeskShell's file explorer.</summary>
    public bool OpenAllFoldersInDeskShell { get; set; }
    /// <summary>Apps opened from the drawer, most recent first.</summary>
    public List<string> RecentApps { get; set; } = new();

    // Island: the pill at the top of the screen
    public bool ShowIsland { get; set; } = true;
    public bool IslandOnAllMonitors { get; set; }
    /// <summary>Keep a strip at the top free so maximised windows sit below the island.</summary>
    public bool IslandReserveSpace { get; set; } = true;
    /// <summary>The island page last shown: music, notifications, windows or programs.</summary>
    public string IslandPage { get; set; } = "windows";
    public IslandStyle Island { get; set; } = new();
    /// <summary>"notch" (a small clock that grows into the Home panel) or "pill" (the classic island with pages).</summary>
    public string IslandLook { get; set; } = "notch";
    public NotchStyle Notch { get; set; } = new();

    [JsonIgnore] public bool UseNotch => IslandLook == "notch";

    /// <summary>Leave Windows' own taskbar on screen, with the tray icons, alongside the island.</summary>
    public bool ShowWindowsTaskbar { get; set; }

    // Dock
    /// <summary>The bottom dock (replaced by the island; off unless turned back on).</summary>
    public bool ShowDock { get; set; }
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

    /// <summary>One-time changes already applied to this settings file.</summary>
    public List<string> Migrations { get; set; } = new();

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
                loaded.Migrate();
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
        fresh.Migrations.Add("island");
        fresh.Save();
        return fresh;
    }

    void Migrate()
    {
        // The island replaced the dock: switch the dock off once (it can be turned back on).
        if (!Migrations.Contains("island"))
        {
            Migrations.Add("island");
            ShowDock = false;
            ShowIsland = true;
        }
        // The notch replaced the pill as the island's look (the pill stays in settings).
        if (!Migrations.Contains("notch"))
        {
            Migrations.Add("notch");
            IslandLook = "notch";
        }
        // The notch's Windows tab came later: put it next to Home.
        if (!Migrations.Contains("notch-windows"))
        {
            Migrations.Add("notch-windows");
            if (!Notch.Tabs.Contains("windows")) Notch.Tabs.Insert(Math.Min(1, Notch.Tabs.Count), "windows");
        }
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

    public void RecordRecentApp(string target)
    {
        RecentApps.RemoveAll(t => string.Equals(t, target, StringComparison.OrdinalIgnoreCase));
        RecentApps.Insert(0, target);
        if (RecentApps.Count > 6) RecentApps.RemoveRange(6, RecentApps.Count - 6);
        Save();
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
