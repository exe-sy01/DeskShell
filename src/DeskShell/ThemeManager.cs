using System.Windows;
using System.Windows.Media;
using Windows.UI.ViewManagement;
using WinColor = Windows.UI.Color;

namespace DeskShell;

/// <summary>The colours of one theme, by role.</summary>
public sealed record ThemePalette(Color Background, Color Card, Color Raised, Color Edge, Color Text, Color TextDim, Color Accent)
{
    public bool IsLight => Luma(Background) > 0.5;
    /// <summary>Buttons and fields under the mouse.</summary>
    public Color RaisedHover => Mix(Raised, Text, 0.08);
    /// <summary>Barely-there text and tracks (empty bars, hints).</summary>
    public Color Faint => Mix(TextDim, Background, 0.45);
    /// <summary>Text drawn on the accent colour.</summary>
    public Color OnAccent => Luma(Accent) > 0.6 ? Colors.Black : Colors.White;

    public static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;

    public static Color Mix(Color a, Color b, double amountOfB) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * amountOfB), (byte)(a.R + (b.R - a.R) * amountOfB),
        (byte)(a.G + (b.G - a.G) * amountOfB), (byte)(a.B + (b.B - a.B) * amountOfB));
}

/// <summary>Frozen brushes for the current theme, for UI built in code.</summary>
public sealed class ThemeBrushes(ThemePalette p)
{
    static Brush B(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public Brush Background { get; } = B(p.Background);
    public Brush Card { get; } = B(p.Card);
    public Brush Raised { get; } = B(p.Raised);
    public Brush RaisedHover { get; } = B(p.RaisedHover);
    public Brush Edge { get; } = B(p.Edge);
    public Brush Text { get; } = B(p.Text);
    public Brush TextDim { get; } = B(p.TextDim);
    public Brush Faint { get; } = B(p.Faint);
    public Brush Accent { get; } = B(p.Accent);
    public Brush OnAccent { get; } = B(p.OnAccent);
}

/// <summary>
/// One theme for everything DeskShell draws. A preset (Black, Graphite, Light, or Windows' own
/// light/dark) gives every colour; your own colours in Settings → Theme override any of them. The
/// accent can follow Windows'. Colours are put into the resources of the app and of each window
/// (so {DynamicResource …} updates at once); code-built UI uses <see cref="Brushes"/> and is
/// rebuilt through <see cref="Changed"/>.
/// </summary>
public static class ThemeManager
{
    static UISettings? _ui;
    static readonly List<ResourceDictionary> _windows = new();
    static string _lastSignature = "";

    /// <summary>Where the theme settings come from (the file explorer's process reads them from the file).</summary>
    public static Func<ThemeSettings> Source { get; set; } = () => App.Settings.Theme;

    public static ThemePalette Current { get; private set; } = Presets["black"];
    public static ThemeBrushes Brushes { get; private set; } = new(Presets["black"]);
    public static bool IsDark => !Current.IsLight;

    /// <summary>Raised on the UI thread after colours change.</summary>
    public static event Action? Changed;

    /// <summary>The names you can set your own colour for, in the order Settings shows them.</summary>
    public static readonly (string Key, string Label)[] Roles =
    [
        ("background", "Background"), ("card", "Cards"), ("raised", "Buttons and fields"), ("edge", "Edges"),
        ("text", "Text"), ("textDim", "Quiet text"), ("accent", "Accent")
    ];

    public static readonly (string Key, string Label)[] PresetNames = [("black", "Black"), ("graphite", "Graphite"), ("light", "Light"), ("windows", "Windows")];

    static Dictionary<string, ThemePalette> Presets => new()
    {
        ["black"] = new(Hex(0xFF000000), Hex(0xFF141414), Hex(0xFF242424), Hex(0x1CFFFFFF), Hex(0xFFFFFFFF), Hex(0xFF9A9A9A), Hex(0xFFFF6A1F)),
        ["graphite"] = new(Hex(0xFF1C1C1E), Hex(0xFF2A2A2D), Hex(0xFF3A3A3E), Hex(0x22FFFFFF), Hex(0xFFF2F2F2), Hex(0xFFA0A0A5), Hex(0xFF0A84FF)),
        ["light"] = new(Hex(0xFFF4F4F6), Hex(0xFFFFFFFF), Hex(0xFFE7E7EB), Hex(0x1A000000), Hex(0xFF1B1B1B), Hex(0xFF68686E), Hex(0xFF0A64D8)),
        // Windows 11's own Fluent greys (what Files uses)
        ["windows-dark"] = new(Hex(0xFF202020), Hex(0xFF2C2C2C), Hex(0xFF3A3A3A), Hex(0x14FFFFFF), Hex(0xFFFFFFFF), Hex(0xFFC5C5C5), Hex(0xFF60CDFF)),
        ["windows-light"] = new(Hex(0xFFF3F3F3), Hex(0xFFF9F9F9), Hex(0xFFE5E5E5), Hex(0x0F000000), Hex(0xFF1B1B1B), Hex(0xFF5E5E5E), Hex(0xFF005FB8))
    };

    public static void Start()
    {
        // UISettings reports both accent and light/dark changes. (Not SystemEvents: its handlers run
        // synchronously on the UI thread, which deadlocked quitting when DeskShell itself broadcast
        // a settings change.)
        try
        {
            _ui = new UISettings();
            _ui.ColorValuesChanged += (_, _) => Application.Current.Dispatcher.InvokeAsync(Refresh);
        }
        catch (Exception ex)
        {
            Log.Error("Can't follow Windows' theme and accent colour", ex);
        }
        Apply(Source());
    }

    /// <summary>Every DeskShell window registers its resources here so it gets the theme.</summary>
    public static void Use(ResourceDictionary resources)
    {
        if (!_windows.Contains(resources)) _windows.Add(resources);
        Put(resources, Current);
    }

    /// <summary>A closed window's resources no longer need updating (and shouldn't be kept alive).</summary>
    public static void Release(ResourceDictionary resources) => _windows.Remove(resources);

    /// <summary>Applies the saved theme again (after Save in settings, or Windows' colours changing).</summary>
    public static void Refresh()
    {
        var before = _lastSignature;
        Apply(Source());
        if (before != _lastSignature) Changed?.Invoke();
    }

    /// <summary>Shows a theme being edited, without telling everything to rebuild.</summary>
    public static void Preview(ThemeSettings theme) => Apply(theme, remember: false);

    /// <summary>The colours a theme comes to (preset, then your own colours, then Windows' accent if chosen).</summary>
    public static ThemePalette Resolve(ThemeSettings theme)
    {
        var dark = Services.SystemToggles.IsDarkMode();
        var key = theme.Preset == "windows" ? dark ? "windows-dark" : "windows-light" : theme.Preset;
        var p = Presets.TryGetValue(key, out var preset) ? preset : Presets["black"];
        Color Own(string role, Color fallback) =>
            theme.Colors.TryGetValue(role, out var hex) && TryParse(hex, out var c) ? c : fallback;
        p = new ThemePalette(Own("background", p.Background), Own("card", p.Card), Own("raised", p.Raised), Own("edge", p.Edge),
            Own("text", p.Text), Own("textDim", p.TextDim), Own("accent", p.Accent));
        if (theme.WindowsAccent || (theme.Preset == "windows" && !theme.Colors.ContainsKey("accent")))
            p = p with { Accent = WindowsAccent(p.IsLight) ?? p.Accent };
        return p;
    }

    /// <summary>The preset's own colour for a role (what Reset goes back to).</summary>
    public static Color PresetColor(string preset, string role) => Resolve(new ThemeSettings { Preset = preset }) switch
    {
        var p => role switch
        {
            "background" => p.Background, "card" => p.Card, "raised" => p.Raised, "edge" => p.Edge,
            "text" => p.Text, "textDim" => p.TextDim, _ => p.Accent
        }
    };

    public static bool TryParse(string? hex, out Color color)
    {
        color = default;
        try
        {
            if (string.IsNullOrWhiteSpace(hex)) return false;
            color = (Color)ColorConverter.ConvertFromString(hex.Trim());
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string ToHex(Color c) => c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    static Color? WindowsAccent(bool light)
    {
        try { return ToWpf((_ui ?? new UISettings()).GetColorValue(light ? UIColorType.AccentDark1 : UIColorType.AccentLight2)); }
        catch { return null; }
    }

    /// <param name="remember">False for a preview: <see cref="Refresh"/> still sees the saved theme as changed afterwards.</param>
    static void Apply(ThemeSettings theme, bool remember = true)
    {
        var p = Resolve(theme);
        Current = p;
        Brushes = new ThemeBrushes(p);
        if (Application.Current != null) Put(Application.Current.Resources, p);
        foreach (var resources in _windows) Put(resources, p);
        if (remember) _lastSignature = p.ToString();
    }

    /// <summary>The palette under every resource name DeskShell's XAML uses.</summary>
    static void Put(ResourceDictionary resources, ThemePalette p)
    {
        void Set(string key, Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources[key] = brush;
        }

        resources["AccentColor"] = p.Accent;
        Set("AccentBrush", p.Accent);
        Set("OnAccentBrush", p.OnAccent);
        Set("BgBrush", p.Background);
        Set("CardBrush", p.Card);
        Set("MenuBrush", p.Card);
        Set("TileBrush", p.Card);
        Set("TileHoverBrush", p.Raised);
        Set("TilePressedBrush", ThemePalette.Mix(p.Raised, p.Background, 0.5));
        Set("EdgeBrush", p.Edge);
        Set("StrongEdgeBrush", p.TextDim with { A = 0x8C });
        Set("TextBrush", p.Text);
        Set("TextDimBrush", p.TextDim);
        Set("DockBrush", p.Background with { A = 0xEB });
        Set("DimBrush", Hex(0x4D000000));
        Set("FieldBrush", p.Raised);
        Set("FieldFocusedBrush", p.Card);
        Set("ThumbOuterBrush", p.Raised);
        Set("FlashBrush", Hex(0x66F7A325));
        Set("DangerBrush", Color.FromRgb(0xC4, 0x2B, 0x1C));
        Set("IslandBrush", p.Background);
        Set("IslandEdgeBrush", p.Edge);
        Set("RaisedBrush", p.Raised);
        Set("RaisedHoverBrush", p.RaisedHover);
        Set("FaintBrush", p.Faint);
    }

    static Color ToWpf(WinColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);
    static Color Hex(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}
