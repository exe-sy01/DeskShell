using System.Windows;
using System.Windows.Media;
using Windows.UI.ViewManagement;
using WinColor = Windows.UI.Color;

namespace DeskShell;

/// <summary>
/// Windows 11 "Fluent" colours, matching the Files app: follows Windows' light/dark mode and
/// your accent colour, and updates live when either changes.
///
/// Brushes are replaced in the app's resources, so anything using {DynamicResource …} updates
/// at once; code-built UI is rebuilt through <see cref="Changed"/>.
/// </summary>
public static class ThemeManager
{
    static UISettings? _ui;

    public static bool IsDark { get; private set; } = true;

    /// <summary>Raised on the UI thread after colours change.</summary>
    public static event Action? Changed;

    public static void Start()
    {
        Apply();
        // UISettings reports both accent and light/dark changes. (Not SystemEvents: its handlers run
        // synchronously on the UI thread, which deadlocked quitting when DeskShell itself broadcast
        // a settings change.)
        try
        {
            _ui = new UISettings();
            _ui.ColorValuesChanged += (_, _) => Application.Current.Dispatcher.InvokeAsync(ApplyAndNotify);
        }
        catch (Exception ex)
        {
            Log.Error("Can't follow theme and accent colour changes", ex);
        }
    }

    static string _lastSignature = "";
    static readonly List<ResourceDictionary> _alwaysDark = new();

    /// <summary>
    /// The island and the shell are always dark (black, like Apple's Dynamic Island), whatever
    /// Windows' mode: their windows get the dark palette in their own resources.
    /// </summary>
    public static void AlwaysDark(ResourceDictionary resources)
    {
        _alwaysDark.Add(resources);
        ApplyPalette(resources, Dark, DarkAccent());
    }

    static Color DarkAccent()
    {
        try { return ToWpf((_ui ?? new UISettings()).GetColorValue(UIColorType.AccentLight2)); }
        catch { return Color.FromRgb(0x60, 0xCD, 0xFF); }
    }

    static void ApplyAndNotify()
    {
        var before = _lastSignature;
        Apply();
        if (before != _lastSignature) Changed?.Invoke();
    }

    static void Apply()
    {
        IsDark = Services.SystemToggles.IsDarkMode();
        var ui = _ui ?? new UISettings();
        Color accent;
        try
        {
            // Fluent uses a lighter shade of the accent on dark backgrounds and a darker one on light.
            accent = ToWpf(ui.GetColorValue(IsDark ? UIColorType.AccentLight2 : UIColorType.AccentDark1));
        }
        catch
        {
            accent = Color.FromRgb(0x60, 0xCD, 0xFF);
        }

        ApplyPalette(Application.Current.Resources, IsDark ? Dark : Light, accent);
        var darkAccent = IsDark ? accent : DarkAccent();
        foreach (var resources in _alwaysDark) ApplyPalette(resources, Dark, darkAccent);

        _lastSignature = $"{IsDark}|{accent}";
    }

    static void ApplyPalette(ResourceDictionary resources, Palette p, Color accent)
    {
        void Set(string key, Color color)
        {
            if (key == "AccentColor")
            {
                resources[key] = color;
                return;
            }
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources[key] = brush;
        }

        Set("AccentColor", accent);
        Set("AccentBrush", accent);
        Set("OnAccentBrush", p.OnAccent);
        Set("BgBrush", p.Bg);
        Set("CardBrush", p.Card);
        Set("MenuBrush", p.Menu);
        Set("TileBrush", p.Tile);
        Set("TileHoverBrush", p.TileHover);
        Set("TilePressedBrush", p.TilePressed);
        Set("EdgeBrush", p.Edge);
        Set("StrongEdgeBrush", p.StrongEdge);
        Set("TextBrush", p.Text);
        Set("TextDimBrush", p.TextDim);
        Set("DockBrush", p.Dock);
        Set("DimBrush", p.Dim);
        Set("FieldBrush", p.Field);
        Set("FieldFocusedBrush", p.FieldFocused);
        Set("ThumbOuterBrush", p.ThumbOuter);
        Set("FlashBrush", p.Flash);
        Set("DangerBrush", Color.FromRgb(0xC4, 0x2B, 0x1C));
        Set("IslandBrush", Hex(0xFF000000));
        Set("IslandEdgeBrush", Hex(0x1FFFFFFF));
    }

    static Color ToWpf(WinColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);
    static Color Hex(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    sealed record Palette(Color Bg, Color Card, Color Menu, Color Tile, Color TileHover, Color TilePressed, Color Edge,
        Color StrongEdge, Color Text, Color TextDim, Color OnAccent, Color Dock, Color Dim, Color Field, Color FieldFocused,
        Color ThumbOuter, Color Flash);

    // Values follow WinUI 3's Fluent theme resources (what Files uses).
    static readonly Palette Dark = new(
        Bg: Hex(0xFF202020),
        Card: Hex(0xFF2C2C2C),
        Menu: Hex(0xFF2C2C2C),
        Tile: Hex(0x0FFFFFFF),
        TileHover: Hex(0x17FFFFFF),
        TilePressed: Hex(0x0AFFFFFF),
        Edge: Hex(0x14FFFFFF),
        StrongEdge: Hex(0x8BFFFFFF),
        Text: Hex(0xFFFFFFFF),
        TextDim: Hex(0xFFC5C5C5),
        OnAccent: Hex(0xFF000000),
        Dock: Hex(0xEB202020),
        Dim: Hex(0x4D000000),
        Field: Hex(0x0FFFFFFF),
        FieldFocused: Hex(0xFF1F1F1F),
        ThumbOuter: Hex(0xFF454545),
        Flash: Hex(0x66F7A325));

    static readonly Palette Light = new(
        Bg: Hex(0xFFF3F3F3),
        Card: Hex(0xFFF9F9F9),
        Menu: Hex(0xFFF9F9F9),
        Tile: Hex(0xB3FFFFFF),
        TileHover: Hex(0x0A000000),
        TilePressed: Hex(0x06000000),
        Edge: Hex(0x0F000000),
        StrongEdge: Hex(0x72000000),
        Text: Hex(0xFF1B1B1B),
        TextDim: Hex(0xFF5E5E5E),
        OnAccent: Hex(0xFFFFFFFF),
        Dock: Hex(0xEBF3F3F3),
        Dim: Hex(0x33000000),
        Field: Hex(0xB3FFFFFF),
        FieldFocused: Hex(0xFFFFFFFF),
        ThumbOuter: Hex(0xFFFFFFFF),
        Flash: Hex(0x66F7A325));
}
