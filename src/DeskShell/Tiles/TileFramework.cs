using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DeskShell.Services;
using ManagedShell;

namespace DeskShell.Tiles;

/// <summary>Everything a tile may need, shared by all tiles.</summary>
public sealed class ShellContext
{
    public required ShellManager Shell { get; init; }
    public required MediaService Media { get; init; }
    public required SystemToggles Toggles { get; init; }
    public required AppIndex Apps { get; init; }
    public required ClipboardHistory Clipboard { get; init; }
    public required Action<bool> CloseShell { get; init; }
    /// <summary>Keeps the shell open but lets an app's own menu (e.g. a tray icon's) show above it.</summary>
    public required Action BeginAppMenu { get; init; }
}

/// <summary>Base for every shell tile. Tiles refresh when the shell opens and pause when it closes.</summary>
public abstract class TileBase : Border
{
    protected TileBase(ShellContext context)
    {
        Context = context;
        CornerRadius = new CornerRadius(14);
        Background = Res<Brush>("TileBrush");
        Padding = new Thickness(14, 12, 14, 12);
        ClipToBounds = false;
    }

    protected ShellContext Context { get; }

    public abstract string Id { get; }
    public abstract string Title { get; }

    /// <summary>Sizes this tile can take, as (columns, rows). The first is the default.</summary>
    public virtual (int W, int H)[] Sizes => [(1, 1), (2, 1)];

    public virtual void OnOpened() { }
    public virtual void OnClosed() { }

    protected static T Res<T>(string key) => (T)Application.Current.FindResource(key);

    protected static TextBlock Text(string text = "", double size = 13, bool bold = false, bool dim = false) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = Res<Brush>(dim ? "TextDimBrush" : "TextBrush"),
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center
    };

    protected static TextBlock Icon(string glyph, double size = 16) => new()
    {
        Text = glyph,
        FontSize = size,
        Style = Res<Style>("Icon")
    };

    protected static Button IconButton(string glyph, string tip, RoutedEventHandler click, double size = 36)
    {
        var b = new Button { Style = Res<Style>("GhostButton"), Width = size, Height = size, ToolTip = tip, Content = Icon(glyph, 14) };
        b.Click += click;
        return b;
    }

    protected void CloseShell(bool immediate = true) => Context.CloseShell(immediate);
}

/// <summary>A tile that is one big toggle button (Wi-Fi, Bluetooth, dark mode…).</summary>
public abstract class ToggleTile : TileBase
{
    protected readonly ToggleButton Button;

    protected ToggleTile(ShellContext context, string glyph, string title) : base(context)
    {
        Padding = new Thickness(0);
        Background = Brushes.Transparent;
        Button = new ToggleButton { Style = Res<Style>("Tile"), Tag = glyph, Content = title, Height = double.NaN };
        Button.Click += (_, _) => OnToggled(Button.IsChecked == true);
        Button.MouseRightButtonUp += (_, e) =>
        {
            if (SettingsPage is { } page)
            {
                CloseShell();
                SystemToggles.OpenSettings(page);
                e.Handled = true;
            }
        };
        Child = Button;
    }

    /// <summary>ms-settings page opened on right-click.</summary>
    protected virtual string? SettingsPage => null;

    protected abstract void OnToggled(bool on);

    protected void Show(bool on, string subtitle, bool enabled = true)
    {
        Button.IsChecked = on;
        Button.IsEnabled = enabled;
        DeskShell.Tile.SetSubtitle(Button, subtitle);
    }
}

/// <summary>Wraps a tile in the grid and draws the editing controls around it.</summary>
public sealed class TileHost : Grid
{
    readonly Border _editFrame;
    readonly TextBlock _sizeLabel;

    public TileHost(TileBase tile, int width, int height)
    {
        Tile = tile;
        ColSpan = width;
        RowSpan = height;
        Children.Add(tile);

        _sizeLabel = new TextBlock { FontSize = 11, Foreground = (Brush)Application.Current.FindResource("TextBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var resize = EditButton("", "Change size");
        resize.Click += (_, _) => CycleSize();
        var hide = EditButton("", "Remove tile");
        hide.Click += (_, _) => RemoveRequested?.Invoke(this);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 6, 0) };
        bar.Children.Add(_sizeLabel);
        bar.Children.Add(resize);
        bar.Children.Add(hide);

        var title = new TextBlock
        {
            Text = tile.Title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource("TextBrush"),
            Margin = new Thickness(12, 10, 0, 0)
        };

        _editFrame = new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1.5),
            BorderBrush = (Brush)Application.Current.FindResource("AccentBrush"),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x1B, 0x20, 0x26)),
            Cursor = System.Windows.Input.Cursors.SizeAll,
            Visibility = Visibility.Collapsed,
            Child = new Grid { Children = { title, bar } }
        };
        Children.Add(_editFrame);
        UpdateSizeLabel();
    }

    public TileBase Tile { get; }
    public int ColSpan { get; private set; }
    public int RowSpan { get; private set; }

    public event Action<TileHost>? RemoveRequested;
    public event Action? Resized;

    public bool Editing
    {
        set
        {
            _editFrame.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            Tile.IsHitTestVisible = !value;
        }
    }

    void CycleSize()
    {
        var sizes = Tile.Sizes;
        var i = Array.FindIndex(sizes, s => s.W == ColSpan && s.H == RowSpan);
        var next = sizes[(i + 1) % sizes.Length];
        ColSpan = next.W;
        RowSpan = next.H;
        UpdateSizeLabel();
        (Parent as UIElement)?.InvalidateMeasure();
        Resized?.Invoke();
    }

    void UpdateSizeLabel() => _sizeLabel.Text = $"{ColSpan}×{RowSpan}";

    static Button EditButton(string glyph, string tip) => new()
    {
        Style = (Style)Application.Current.FindResource("GhostButton"),
        Width = 30,
        Height = 30,
        ToolTip = tip,
        Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
        Content = new TextBlock { Text = glyph, Style = (Style)Application.Current.FindResource("Icon"), FontSize = 12 }
    };
}

/// <summary>
/// Lays tiles out on a fixed-column grid, in order, each in the first free spot it fits
/// (like CSS grid's dense auto-flow).
/// </summary>
public sealed class TilePanel : Panel
{
    public int Columns { get; set; } = 4;
    public double RowHeight { get; set; } = 74;
    public double Gap { get; set; } = 10;

    readonly Dictionary<UIElement, Rect> _slots = new();

    public IReadOnlyDictionary<UIElement, Rect> Slots => _slots;

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? 900 : available.Width;
        var cell = (width - Gap * (Columns - 1)) / Columns;
        var used = new List<bool[]>();
        _slots.Clear();
        var rows = 0;

        foreach (UIElement child in InternalChildren)
        {
            var host = child as TileHost;
            var w = Math.Min(host?.ColSpan ?? 1, Columns);
            var h = host?.RowSpan ?? 1;
            var (col, row) = FindSpot(used, w, h);
            for (var r = row; r < row + h; r++)
            {
                while (used.Count <= r) used.Add(new bool[Columns]);
                for (var c = col; c < col + w; c++) used[r][c] = true;
            }
            rows = Math.Max(rows, row + h);

            var rect = new Rect(col * (cell + Gap), row * (RowHeight + Gap), w * cell + (w - 1) * Gap, h * RowHeight + (h - 1) * Gap);
            _slots[child] = rect;
            child.Measure(rect.Size);
        }

        return new Size(width, rows == 0 ? 0 : rows * RowHeight + (rows - 1) * Gap);
    }

    (int Col, int Row) FindSpot(List<bool[]> used, int w, int h)
    {
        for (var row = 0; ; row++)
        for (var col = 0; col + w <= Columns; col++)
        {
            var free = true;
            for (var r = row; r < row + h && free; r++)
            for (var c = col; c < col + w && free; c++)
                if (r < used.Count && used[r][c]) free = false;
            if (free) return (col, row);
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (UIElement child in InternalChildren)
            if (_slots.TryGetValue(child, out var rect)) child.Arrange(rect);
        return finalSize;
    }
}
