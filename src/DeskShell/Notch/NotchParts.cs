using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DeskShell.Notch;

/// <summary>The notch's outline: flat against the top of the screen, flaring into it with two inward curves, rounded at the bottom.</summary>
static class NotchShape
{
    /// <summary>The filled shape. <paramref name="width"/> is the body without its two flares.</summary>
    public static Geometry Fill(double left, double width, double height, double ear, double radius)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(left, 0), true, true);
            Trace(c, left, width, height, ear, radius);
        }
        g.Freeze();
        return g;
    }

    /// <summary>The edge only (no top line), for the glow.</summary>
    public static Geometry Edge(double left, double width, double height, double ear, double radius)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(left, 0), false, false);
            Trace(c, left, width, height, ear, radius);
        }
        g.Freeze();
        return g;
    }

    static void Trace(StreamGeometryContext c, double left, double width, double height, double ear, double radius)
    {
        var l = left + ear;           // the body's left side
        var r = left + ear + width;   // its right side
        radius = Math.Min(radius, Math.Min(width, height) / 2);
        var e = Math.Min(ear, height / 2);
        c.QuadraticBezierTo(new Point(l, 0), new Point(l, e), true, true);
        c.LineTo(new Point(l, height - radius), true, true);
        c.ArcTo(new Point(l + radius, height), new Size(radius, radius), 0, false, SweepDirection.Counterclockwise, true, true);
        c.LineTo(new Point(r - radius, height), true, true);
        c.ArcTo(new Point(r, height - radius), new Size(radius, radius), 0, false, SweepDirection.Counterclockwise, true, true);
        c.LineTo(new Point(r, e), true, true);
        c.QuadraticBezierTo(new Point(r, 0), new Point(r + ear, 0), true, true);
    }
}

/// <summary>A round gauge: a faint track with a coloured arc for the value, and the value in the middle.</summary>
sealed class Ring : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(Ring),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Thickness { get; set; } = 5;
    public Brush Track { get; set; } = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
    public Brush Fill { get; set; } = Brushes.OrangeRed;
    public double TextSize { get; set; } = 12;

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness * 2) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var r = size / 2 - Thickness / 2;
        dc.DrawEllipse(null, new Pen(Track, Thickness), center, r, r);

        var v = Math.Clamp(Value, 0, 100) / 100;
        if (v > 0.001)
        {
            var pen = new Pen(Fill, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (v >= 0.999) dc.DrawEllipse(null, pen, center, r, r);
            else
            {
                var angle = v * 2 * Math.PI;
                var start = new Point(center.X, center.Y - r);
                var end = new Point(center.X + r * Math.Sin(angle), center.Y - r * Math.Cos(angle));
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(start, false, false);
                    c.ArcTo(end, new Size(r, r), 0, v > 0.5, SweepDirection.Clockwise, true, false);
                }
                g.Freeze();
                dc.DrawGeometry(null, pen, g);
            }
        }

        var text = new FormattedText($"{Math.Round(Value)}%", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            TextSize, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
    }
}

/// <summary>Small builders shared by the panel's cards and pages.</summary>
static class UI
{
    public static readonly Brush Card = Frozen(Color.FromRgb(0x14, 0x14, 0x14));
    public static readonly Brush CardEdge = Frozen(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
    public static readonly Brush Raised = Frozen(Color.FromRgb(0x24, 0x24, 0x24));
    public static readonly Brush RaisedHover = Frozen(Color.FromRgb(0x30, 0x30, 0x30));
    public static readonly Brush Text = Brushes.White;
    public static readonly Brush Dim = Frozen(Color.FromRgb(0x9A, 0x9A, 0x9A));
    public static readonly Brush Faint = Frozen(Color.FromRgb(0x5C, 0x5C, 0x5C));
    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static Color Parse(string hex, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return fallback; }
    }

    public static TextBlock Label(string text, double size = 13, bool bold = false, Brush? brush = null, FontFamily? font = null) => new()
    {
        Text = text,
        FontSize = size,
        FontFamily = font ?? Font,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = brush ?? Text,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center
    };

    public static TextBlock Glyph(string glyph, double size = 14, Brush? brush = null) => new()
    {
        Text = glyph,
        FontFamily = Icons,
        FontSize = size,
        Foreground = brush ?? Text,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center
    };

    /// <summary>A card: dark rounded box with a small icon and title on top.</summary>
    public static Border CardBox(string glyph, string title, UIElement body, Action? click = null)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        head.Children.Add(Glyph(glyph, 11, Dim));
        head.Children.Add(Label(title.ToUpperInvariant(), 11, bold: true, brush: Dim));
        ((TextBlock)head.Children[1]).Margin = new Thickness(7, 0, 0, 0);
        var stack = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(head, Dock.Top);
        stack.Children.Add(head);
        stack.Children.Add(body);
        var card = new Border
        {
            Background = Card,
            BorderBrush = CardEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(16, 13, 14, 12),
            Child = stack
        };
        if (click != null)
        {
            card.Cursor = System.Windows.Input.Cursors.Hand;
            card.MouseLeftButtonUp += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject d && FindButton(d, card)) return; // a button inside handled it
                click();
            };
        }
        return card;
    }

    static bool FindButton(DependencyObject d, DependencyObject stop)
    {
        for (var x = d; x != null && x != stop; x = VisualTreeHelper.GetParent(x) ?? LogicalTreeHelper.GetParent(x))
            if (x is System.Windows.Controls.Primitives.ButtonBase or TextBox) return true;
        return false;
    }

    /// <summary>A round icon button (play, add, copy…).</summary>
    public static Button Round(string glyph, double size, Action click, string? tip = null, bool light = false)
    {
        var bg = new Border { CornerRadius = new CornerRadius(size / 2), Background = light ? Brushes.White : Raised, Width = size, Height = size };
        bg.Child = Glyph(glyph, size * 0.36, light ? Brushes.Black : Text);
        var button = new Button { Content = bg, ToolTip = tip, Cursor = System.Windows.Input.Cursors.Hand, Template = Bare(), Focusable = false };
        if (tip != null) System.Windows.Automation.AutomationProperties.SetName(button, tip);
        button.MouseEnter += (_, _) => { if (!light) bg.Background = RaisedHover; };
        button.MouseLeave += (_, _) => { if (!light) bg.Background = Raised; };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>A text button shaped like a pill.</summary>
    public static Button Pill(string text, Action click, string? glyph = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph != null) row.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 12, Foreground = Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
        row.Children.Add(Label(text, 12.5));
        var bg = new Border { CornerRadius = new CornerRadius(14), Background = Raised, Padding = new Thickness(13, 6, 13, 6), Child = row };
        var button = new Button { Content = bg, Cursor = System.Windows.Input.Cursors.Hand, Template = Bare(), Focusable = false, Margin = new Thickness(0, 0, 8, 0) };
        button.MouseEnter += (_, _) => bg.Background = RaisedHover;
        button.MouseLeave += (_, _) => bg.Background = Raised;
        button.Click += (_, _) => click();
        return button;
    }

    public static ControlTemplate Bare()
    {
        var t = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        t.Seal();
        return t;
    }

    /// <summary>A dark rounded text box with grey hint text over it until something is typed.</summary>
    public static Grid Field(string hint, out TextBox field)
    {
        var box = field = new TextBox
        {
            Background = Raised,
            Foreground = Text,
            CaretBrush = Text,
            BorderThickness = new Thickness(0),
            FontFamily = Font,
            FontSize = 12.5,
            Padding = new Thickness(10, 7, 10, 7),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var hintText = Label(hint, 12.5, brush: Faint);
        hintText.IsHitTestVisible = false;
        hintText.Margin = new Thickness(12, 0, 0, 0);
        var template = new ControlTemplate(typeof(TextBox));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var host = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
        host.SetValue(ScrollViewer.MarginProperty, new Thickness(9, 7, 9, 7));
        host.SetValue(ScrollViewer.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(host);
        template.VisualTree = border;
        box.Template = template;

        box.TextChanged += (_, _) => hintText.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        return new Grid { Children = { box, hintText } };
    }

    public static string Ago(DateTime when)
    {
        var span = DateTime.Now - when;
        return span.TotalMinutes < 1 ? "now" : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m" : span.TotalDays < 1 ? $"{(int)span.TotalHours}h" : $"{(int)span.TotalDays}d";
    }
}
