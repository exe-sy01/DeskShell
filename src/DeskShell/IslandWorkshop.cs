using System.Text.Json;

namespace DeskShell;

/// <summary>
/// Reads the settings Island Workshop produces ("Copy settings" there, then "Paste from Island
/// Workshop" in DeskShell's settings). Unknown or missing values keep what DeskShell has.
/// </summary>
public static class IslandWorkshop
{
    static readonly HashSet<string> KnownPages = ["notifications", "music", "windows", "programs", "clock"];

    /// <summary>Applies workshop JSON to <paramref name="settings"/>; returns an error message, or null.</summary>
    public static string? Apply(string json, Settings settings)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement; }
        catch { return "That isn't Island Workshop's settings (copy them with \"Copy settings\")."; }
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("settings", out var s) || s.ValueKind != JsonValueKind.Object)
            return "That isn't Island Workshop's settings (copy them with \"Copy settings\").";

        var island = settings.Island;
        double Num(string key, double current) => s.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : current;
        bool Bool(string key, bool current) => s.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : current;
        string Str(string key, string current, params string[] allowed) =>
            s.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && allowed.Contains(v.GetString()) ? v.GetString()! : current;

        island.Width = Math.Clamp(Num("width", island.Width), 240, 800);
        island.Height = Math.Clamp(Num("height", island.Height), 24, 72);
        island.Roundness = Math.Clamp(Num("roundness", island.Roundness), 0, 100);
        island.Surface = Str("surface", island.Surface, "black", "glass", "tint");
        island.Edge = Str("edge", island.Edge, "none", "subtle", "accent");
        island.Shadow = Str("shadow", island.Shadow, "none", "soft", "strong");
        island.TopGap = Math.Clamp(Num("topGap", island.TopGap), 0, 40);
        settings.IslandOnAllMonitors = Str("monitors", settings.IslandOnAllMonitors ? "all" : "main", "main", "all") == "all";
        settings.IslandReserveSpace = Bool("reserveSpace", settings.IslandReserveSpace);
        island.HideFullscreen = Bool("hideFullscreen", island.HideFullscreen);
        island.Peek = Bool("peek", island.Peek);

        island.StartOnLastPage = Str("startPage", island.StartOnLastPage ? "last" : "first", "last", "first") == "last";
        island.Arrows = Str("arrows", island.Arrows, "always", "hover", "hidden");
        island.PageDots = Str("indicator", island.PageDots ? "dots" : "none", "none", "dots") == "dots";
        island.WheelScrollsIconsFirst = Str("wheel", island.WheelScrollsIconsFirst ? "icons-first" : "pages", "icons-first", "pages") == "icons-first";

        island.MusicArt = Bool("musicArt", island.MusicArt);
        island.MusicArtist = Bool("musicArtist", island.MusicArtist);
        island.MusicControls = Bool("musicControls", island.MusicControls);
        island.MusicBars = Bool("musicBars", island.MusicBars);
        island.NotificationText = Bool("notifBody", island.NotificationText);
        island.NotificationCount = Bool("notifCount", island.NotificationCount);
        island.WindowsFromAllMonitors = Str("windowsScope", island.WindowsFromAllMonitors ? "all" : "monitor", "all", "monitor") == "all";
        island.ActiveWindowTitle = Bool("activeTitle", island.ActiveWindowTitle);
        island.IconSize = Math.Clamp(Num("iconSize", island.IconSize), 12, 28);

        island.JumpToMusic = Bool("autoMusic", island.JumpToMusic);
        island.JumpToNotifications = Bool("autoNotify", island.JumpToNotifications);
        island.GrowForNotifications = Bool("notifyGrow", island.GrowForNotifications);
        island.MomentSeconds = Math.Clamp(Num("momentSeconds", island.MomentSeconds), 1, 30);

        island.OpenWith = Str("openWith", island.OpenWith, "double", "single", "hover");
        island.HoverGrow = Math.Clamp(Num("hoverGrow", island.HoverGrow), 0, 15);
        island.ExpandMs = Math.Clamp(Num("animMs", island.ExpandMs), 100, 1200);
        island.Bounce = Math.Clamp(Num("bounce", island.Bounce), 0, 80);
        island.Dim = Math.Clamp(Num("dim", island.Dim), 0, 90);
        island.ShellRadius = Math.Clamp(Num("shellRadius", island.ShellRadius), 0, 48);
        island.SidePanels = Bool("sidePanels", island.SidePanels);

        if (root.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array)
        {
            var on = pages.EnumerateArray()
                .Where(p => p.ValueKind == JsonValueKind.Object && p.TryGetProperty("on", out var o) && o.ValueKind == JsonValueKind.True
                            && p.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && KnownPages.Contains(id.GetString()!))
                .Select(p => p.GetProperty("id").GetString()!)
                .Distinct()
                .ToList();
            if (on.Count > 0) island.Pages = on;
        }
        return null;
    }
}
