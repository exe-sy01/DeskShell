using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace DeskShell.Notch;

/// <summary>
/// Sage's face in the notch: a glowing orb whose colour says what Sage is doing, with a ring that moves
/// with its voice while it talks. It only redraws when something changes (and gently pulses while Sage
/// thinks, a few times a second, only while you can see it).
/// </summary>
sealed class SageOrb : FrameworkElement
{
    public static readonly Color Calm = Color.FromRgb(0x5E, 0xC8, 0xD8);
    public static readonly Color Listen = Color.FromRgb(0x4F, 0xA8, 0xFF);
    public static readonly Color Think = Color.FromRgb(0xA7, 0x7B, 0xFF);
    public static readonly Color Talk = Color.FromRgb(0x5C, 0xE0, 0x9A);
    public static readonly Color Ask = Color.FromRgb(0xFF, 0xD2, 0x4A);
    public static readonly Color Trouble = Color.FromRgb(0xFF, 0x6B, 0x6B);
    public static readonly Color Asleep = Color.FromRgb(0x6B, 0x6B, 0x74);

    readonly DispatcherTimer _pulse = new() { Interval = TimeSpan.FromMilliseconds(100) };
    double _phase;
    double _level;
    Color _color = Asleep;
    bool _busy;

    public SageOrb(double size)
    {
        Width = Height = size;
        _pulse.Tick += (_, _) => { _phase += 0.35; InvalidateVisual(); };
        IsVisibleChanged += (_, _) => Animate();
    }

    /// <summary>Picks the colour from Sage's state (and whether it's waiting on you).</summary>
    public void Show(string state, bool waitingOnYou, bool speaking)
    {
        var color = ColorFor(state, waitingOnYou, speaking);
        var busy = state is "Thinking" or "Acting" or "Researching" or "Observing" or "Listening";
        if (color == _color && busy == _busy) return;
        _color = color;
        _busy = busy;
        if (!speaking) _level = 0;
        Animate();
        InvalidateVisual();
    }

    public static Color ColorFor(string state, bool waitingOnYou, bool speaking) =>
        waitingOnYou ? Ask
        : speaking ? Talk
        : state switch
        {
            "Offline" => Asleep,
            "Listening" => Listen,
            "Thinking" or "Acting" or "Researching" or "Observing" => Think,
            "Speaking" => Talk,
            "Error" => Trouble,
            _ => Calm
        };

    /// <summary>How loud Sage is right now (0..1); the ring follows it.</summary>
    public void SetLevel(double level)
    {
        level = Math.Round(Math.Clamp(level, 0, 1) * 12) / 12;
        if (Math.Abs(level - _level) < 0.01) return;
        _level = level;
        if (IsVisible) InvalidateVisual();
    }

    void Animate()
    {
        if (_busy && IsVisible) _pulse.Start();
        else _pulse.Stop();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var r = Math.Min(ActualWidth, ActualHeight) / 2;
        if (r <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var breathe = _busy ? 0.5 + 0.5 * Math.Sin(_phase) : 0;
        var core = r * (0.62 + 0.04 * breathe);

        // Soft halo, bigger when it talks or thinks.
        var halo = core + r * (0.18 + 0.2 * _level + 0.08 * breathe);
        var glow = new RadialGradientBrush(
            Color.FromArgb(0x70, _color.R, _color.G, _color.B),
            Color.FromArgb(0x00, _color.R, _color.G, _color.B));
        dc.DrawEllipse(glow, null, center, halo, halo);

        // The orb: bright at the top left, deeper below.
        var body = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.35, 0.3),
            Center = new Point(0.45, 0.4),
            RadiusX = 0.75,
            RadiusY = 0.75,
            GradientStops =
            {
                new GradientStop(Mix(_color, Colors.White, 0.55), 0),
                new GradientStop(_color, 0.55),
                new GradientStop(Mix(_color, Colors.Black, 0.45), 1)
            }
        };
        dc.DrawEllipse(body, null, center, core, core);

        // A ring that follows Sage's voice.
        if (_level > 0.02)
        {
            var ring = core + 3 + r * 0.22 * _level;
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0xB0, _color.R, _color.G, _color.B)), 1.5), center, ring, ring);
        }

        // A little shine.
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)), null,
            new Point(center.X - core * 0.32, center.Y - core * 0.38), core * 0.16, core * 0.11);
    }

    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    /// <summary>A small still orb for the notch's pop-ups.</summary>
    public static ImageSource Icon(Color color)
    {
        var body = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.35, 0.3),
            Center = new Point(0.45, 0.4),
            RadiusX = 0.75,
            RadiusY = 0.75,
            GradientStops =
            {
                new GradientStop(Mix(color, Colors.White, 0.55), 0),
                new GradientStop(color, 0.55),
                new GradientStop(Mix(color, Colors.Black, 0.45), 1)
            }
        };
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 36, 36))));
        group.Children.Add(new GeometryDrawing(body, null, new EllipseGeometry(new Point(18, 18), 14, 14)));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)), null, new EllipseGeometry(new Point(13.5, 12.5), 2.2, 1.6)));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
