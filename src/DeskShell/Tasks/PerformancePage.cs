using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DeskShell.Services;
using Microsoft.Win32;

namespace DeskShell.Tasks;

/// <summary>
/// A small line graph of the last few dozen values (newest on the right), with the area under the
/// line filled. Each series is one <see cref="StreamGeometry"/> drawn in <see cref="OnRender"/>, so
/// a graph costs one draw call per series per sample instead of an element per point; it only
/// redraws when a value is pushed or its size or colours change.
/// </summary>
public sealed class HistoryGraph : FrameworkElement
{
    /// <summary>The main line (and, faded, its fill). Follows the theme's accent unless set.</summary>
    public static readonly DependencyProperty LineBrushProperty = Paint(nameof(LineBrush));

    /// <summary>The optional second line (disk write, network upload). Follows the theme's quiet text unless set.</summary>
    public static readonly DependencyProperty SecondBrushProperty = Paint(nameof(SecondBrush));

    /// <summary>The area under the main line. When unset it's the line colour, faded.</summary>
    public static readonly DependencyProperty FillBrushProperty = Paint(nameof(FillBrush));

    /// <summary>The 25/50/75% guide lines. Follows the theme's faint colour unless set.</summary>
    public static readonly DependencyProperty GridBrushProperty = Paint(nameof(GridBrush));

    static DependencyProperty Paint(string name) => DependencyProperty.Register(name, typeof(Brush), typeof(HistoryGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((HistoryGraph)d).ForgetPaint()));

    readonly double[] _first, _second;
    int _count, _head;
    bool _hasSecond;
    double _max = 100, _minScale = 1, _thickness = 2;
    bool _showGrid = true;
    Pen? _linePen, _secondPen, _gridPen;
    Brush? _fill;

    public HistoryGraph() : this(60) { }

    /// <param name="capacity">How many values are kept; at one sample a second, 60 is the last minute.</param>
    public HistoryGraph(int capacity)
    {
        capacity = Math.Max(2, capacity);
        _first = new double[capacity];
        _second = new double[capacity];
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        // Clicks go to whatever the graph sits on (a card), and nothing is hit-tested per point.
        IsHitTestVisible = false;
        SetResourceReference(LineBrushProperty, "AccentBrush");
        SetResourceReference(SecondBrushProperty, "TextDimBrush");
        SetResourceReference(GridBrushProperty, "FaintBrush");
        ScaleMax = _max;
    }

    public Brush? LineBrush { get => (Brush?)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public Brush? SecondBrush { get => (Brush?)GetValue(SecondBrushProperty); set => SetValue(SecondBrushProperty, value); }
    public Brush? FillBrush { get => (Brush?)GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }
    public Brush? GridBrush { get => (Brush?)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }

    /// <summary>How many values the graph remembers (its width in samples).</summary>
    public int Capacity => _first.Length;

    /// <summary>How many values it holds right now.</summary>
    public int Count => _count;

    /// <summary>The newest main value (0 when empty).</summary>
    public double Latest => _count == 0 ? 0 : _first[(_head - 1 + Capacity) % Capacity];

    /// <summary>
    /// The value at the top of the graph. Percentages use a fixed 100; set 0 (or less) for rates,
    /// which then scale to the largest value currently on screen, so a quiet minute still shows shape.
    /// </summary>
    public double Max
    {
        get => _max;
        set { _max = value; Rescale(); InvalidateVisual(); }
    }

    /// <summary>When auto-scaling, the top never drops below this, so near-idle noise doesn't fill the graph.</summary>
    public double MinScale
    {
        get => _minScale;
        set { _minScale = Math.Max(0, value); Rescale(); InvalidateVisual(); }
    }

    /// <summary>The value the top edge currently stands for (fixed <see cref="Max"/>, or the auto scale). For a label.</summary>
    public double ScaleMax { get; private set; }

    /// <summary>Guide lines at 25/50/75%. Off for tiny sparklines, where they'd just be noise.</summary>
    public bool ShowGrid
    {
        get => _showGrid;
        set { _showGrid = value; InvalidateVisual(); }
    }

    /// <summary>Line width in device-independent pixels.</summary>
    public double Thickness
    {
        get => _thickness;
        set { _thickness = Math.Max(0.5, value); ForgetPaint(); InvalidateVisual(); }
    }

    /// <summary>Adds the newest value (and optionally the second series' value) and redraws once.</summary>
    public void Push(double value, double? second = null)
    {
        _first[_head] = Clean(value);
        _second[_head] = second is double s ? Clean(s) : 0;
        if (second != null) _hasSecond = true;
        _head = (_head + 1) % Capacity;
        if (_count < Capacity) _count++;
        Rescale();
        InvalidateVisual();
    }

    /// <summary>Forgets every value (when the Task Manager closes, so reopening starts fresh).</summary>
    public void Clear()
    {
        _count = 0;
        _head = 0;
        _hasSecond = false;
        Rescale();
        InvalidateVisual();
    }

    static double Clean(double v) => double.IsFinite(v) ? Math.Max(0, v) : 0;

    void Rescale()
    {
        if (_max > 0)
        {
            ScaleMax = _max;
            return;
        }
        // Until the buffer is full the values sit at 0.._count-1; once full, every slot is live.
        double peak = 0;
        for (var i = 0; i < _count; i++)
        {
            peak = Math.Max(peak, _first[i]);
            if (_hasSecond) peak = Math.Max(peak, _second[i]);
        }
        // A little headroom so the peak isn't flattened against the top edge.
        ScaleMax = Math.Max(Math.Max(_minScale, 1e-9), peak * 1.1);
    }

    /// <summary>Pens and the fill are built from the brushes once, then reused until a brush or the width changes.</summary>
    void ForgetPaint()
    {
        _linePen = _secondPen = _gridPen = null;
        _fill = null;
    }

    void EnsurePaint()
    {
        if (_linePen != null) return;
        var line = LineBrush ?? System.Windows.Media.Brushes.DeepSkyBlue;
        _linePen = MakePen(line, _thickness);
        _secondPen = MakePen(SecondBrush ?? Fade(line, 0.6), _thickness * 0.8);
        _gridPen = MakePen(Fade(GridBrush ?? System.Windows.Media.Brushes.Gray, 0.5), 1);
        _fill = FillBrush ?? Gradient(line);
    }

    static Pen MakePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (pen.CanFreeze) pen.Freeze();
        return pen;
    }

    /// <summary>The same colour at a fraction of its opacity.</summary>
    static Brush Fade(Brush brush, double opacity)
    {
        if (brush is SolidColorBrush solid)
        {
            var c = solid.Color;
            var faded = new SolidColorBrush(Color.FromArgb((byte)(c.A * opacity * solid.Opacity), c.R, c.G, c.B));
            faded.Freeze();
            return faded;
        }
        var copy = brush.CloneCurrentValue();
        copy.Opacity *= opacity;
        if (copy.CanFreeze) copy.Freeze();
        return copy;
    }

    /// <summary>The fill under the line: the line colour at about a quarter opacity, a touch stronger at the top.</summary>
    static Brush Gradient(Brush line)
    {
        if (line is not SolidColorBrush solid) return Fade(line, 0.25);
        var c = solid.Color;
        var a = c.A * solid.Opacity;
        var brush = new LinearGradientBrush(
            Color.FromArgb((byte)(a * 0.34), c.R, c.G, c.B),
            Color.FromArgb((byte)(a * 0.12), c.R, c.G, c.B),
            90);
        brush.Freeze();
        return brush;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        EnsurePaint();

        if (_showGrid)
            for (var i = 1; i <= 3; i++)
            {
                var y = Math.Round(h * i / 4) + 0.5;
                dc.DrawLine(_gridPen!, new Point(0, y), new Point(w, y));
            }

        if (_count < 2 || ScaleMax <= 0) return;
        dc.DrawGeometry(_fill, _linePen, Build(_first, w, h, filled: true));
        if (_hasSecond) dc.DrawGeometry(null, _secondPen, Build(_second, w, h, filled: false));
    }

    /// <summary>
    /// One figure for a series: the line through every point, and for the main series the drop to
    /// the bottom edge (unstroked) so the same geometry fills the area under the line.
    /// </summary>
    StreamGeometry Build(double[] values, double w, double h, bool filled)
    {
        var step = w / (Capacity - 1);
        var inset = _thickness / 2;
        var span = Math.Max(1, h - 2 * inset);
        var start = (_head - _count + Capacity) % Capacity;
        var x0 = w - (_count - 1) * step;

        var geometry = new StreamGeometry();
        using (var c = geometry.Open())
        {
            Point At(int k)
            {
                var v = values[(start + k) % Capacity];
                return new Point(x0 + k * step, h - inset - Math.Min(1, v / ScaleMax) * span);
            }

            var first = At(0);
            if (filled)
            {
                c.BeginFigure(new Point(first.X, h), isFilled: true, isClosed: false);
                c.LineTo(first, isStroked: false, isSmoothJoin: false);
            }
            else
                c.BeginFigure(first, isFilled: false, isClosed: false);

            var last = first;
            for (var k = 1; k < _count; k++)
            {
                last = At(k);
                c.LineTo(last, isStroked: true, isSmoothJoin: true);
            }
            if (filled) c.LineTo(new Point(last.X, h), isStroked: false, isSmoothJoin: false);
        }
        geometry.Freeze();
        return geometry;
    }
}

/// <summary>
/// The Task Manager's Performance page: a column of cards (CPU, memory, disk, network, GPU), each
/// with a sparkline and its current value, and the selected one shown large on the right with a
/// minute of history and the details behind it. It doesn't sample anything itself: the window
/// already samples once a second while it's open and hands each sample to <see cref="Update"/>.
/// </summary>
public sealed class PerformancePage : Grid
{
    enum Kind { Cpu, Memory, Disk, Network, Gpu }

    /// <summary>Everything belonging to one resource: its card on the left and its panel on the right.</summary>
    sealed class Section(Kind kind, string name)
    {
        public readonly Kind Kind = kind;
        public readonly string Name = name;
        public Border Card = null!;
        public TextBlock CardValue = null!, CardSub = null!;
        public HistoryGraph Spark = null!;
        public Grid Detail = null!;
        public TextBlock Subtitle = null!, Big = null!, Caption = null!, ScaleTop = null!;
        public HistoryGraph Graph = null!;
        public Grid Axis = null!;
        public readonly Dictionary<string, TextBlock> Facts = new();
    }

    readonly List<Section> _sections = new();
    readonly Section _cpu, _memory, _disk, _network, _gpu;
    Section _selected;
    MachineSample? _last;
    string _adapters = "", _adapterDetail = "";
    long _adaptersAt = -1;

    public PerformancePage()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(272) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 320 });

        var cards = new StackPanel();
        var scroller = new ScrollViewer
        {
            Content = cards,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false
        };
        Children.Add(scroller);

        var panels = new Grid();
        var detail = new Border { CornerRadius = new CornerRadius(18), Padding = new Thickness(28, 24, 28, 18), BorderThickness = new Thickness(1), Child = panels };
        detail.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        detail.SetResourceReference(Border.BorderBrushProperty, "EdgeBrush");
        SetColumn(detail, 2);
        Children.Add(detail);

        _cpu = Add(Kind.Cpu, "CPU", "", ["Utilisation", "Speed", "Processes", "Threads", "Handles", "Up time", "Logical processors"]);
        _memory = Add(Kind.Memory, "Memory", "", ["In use", "Available", "Committed", "Total"]);
        _disk = Add(Kind.Disk, "Disk", "", ["Active time", "Read", "Write"]);
        _network = Add(Kind.Network, "Network", "", ["Receive", "Send", "Connected"]);
        _gpu = Add(Kind.Gpu, "GPU", "", ["Utilisation", "Adapter", "Dedicated memory"]);

        // Rates have no natural top, so those graphs scale to what's on screen (with a floor so idle
        // noise stays small): 1 MB/s for disks, 100 Kbps for the network.
        _disk.Graph.Max = 0;
        _disk.Graph.MinScale = 1_048_576;
        _network.Spark.Max = _network.Graph.Max = 0;
        _network.Spark.MinScale = _network.Graph.MinScale = 12_500;

        AddLegend(_disk, "Read", "Write");
        AddLegend(_network, "Receive", "Send");

        foreach (var s in _sections)
        {
            cards.Children.Add(s.Card);
            panels.Children.Add(s.Detail);
        }

        _selected = _cpu;
        Select(_cpu);
        Clear();
    }

    // ---------- public ----------

    /// <summary>Takes this second's sample: every graph gets a point, the cards and the visible panel their numbers.</summary>
    public void Update(MachineSample s)
    {
        _last = s;
        var memory = Percent(s.MemoryUsed, s.MemoryTotal);

        _cpu.Spark.Push(s.Cpu);
        _cpu.Graph.Push(s.Cpu);
        _memory.Spark.Push(memory);
        _memory.Graph.Push(memory);
        _disk.Spark.Push(s.DiskActive);
        _disk.Graph.Push(s.DiskRead, s.DiskWrite);
        _network.Spark.Push(s.NetDown, s.NetUp);
        _network.Graph.Push(s.NetDown, s.NetUp);
        _gpu.Spark.Push(s.Gpu ?? 0);
        _gpu.Graph.Push(s.Gpu ?? 0);

        _cpu.CardValue.Text = $"{s.Cpu:0}%";
        _cpu.CardSub.Text = $"{s.Processes:N0} processes";
        _memory.CardValue.Text = $"{memory:0}%";
        _memory.CardSub.Text = $"{Gb(s.MemoryUsed)} of {Gb(s.MemoryTotal)}";
        _disk.CardValue.Text = $"{s.DiskActive:0}%";
        _disk.CardSub.Text = $"R {ByteRate(s.DiskRead)} · W {ByteRate(s.DiskWrite)}";
        _network.CardValue.Text = $"↓ {BitRate(s.NetDown)}";
        _network.CardSub.Text = $"↑ {BitRate(s.NetUp)}";
        _gpu.CardValue.Text = s.Gpu is double g ? $"{g:0}%" : "—";
        _gpu.CardSub.Text = s.Gpu is null ? "Not available" : Hardware.GpuName;

        Refresh(_selected);
    }

    /// <summary>Forgets all history and numbers, so the next time the window opens it doesn't join an old minute to a new one.</summary>
    public void Clear()
    {
        _last = null;
        _adaptersAt = -1;
        foreach (var s in _sections)
        {
            s.Spark.Clear();
            s.Graph.Clear();
            s.CardValue.Text = "—";
            s.CardSub.Text = "";
        }
        Refresh(_selected);
    }

    // ---------- the selected panel ----------

    /// <summary>Fills in the right-hand panel. Only the visible one is kept current; the others catch up when picked.</summary>
    void Refresh(Section x)
    {
        var s = _last;
        x.Big.FontSize = 56;
        x.ScaleTop.Text = x.Graph.Max > 0 ? "100%" : x.Kind == Kind.Network ? BitRate(x.Graph.ScaleMax) : ByteRate(x.Graph.ScaleMax);

        switch (x.Kind)
        {
            case Kind.Cpu:
                x.Subtitle.Text = Hardware.CpuName;
                x.Big.Text = s is null ? "—" : $"{s.Cpu:0}%";
                x.Caption.Text = "Utilisation";
                Fact(x, "Utilisation", s is null ? "—" : $"{s.Cpu:0}%");
                Fact(x, "Speed", Hardware.Speed is { } speed ? $"{speed} base" : "—");
                Fact(x, "Processes", s?.Processes.ToString("N0") ?? "—");
                Fact(x, "Threads", s?.Threads.ToString("N0") ?? "—");
                Fact(x, "Handles", s?.Handles.ToString("N0") ?? "—");
                Fact(x, "Up time", Uptime());
                Fact(x, "Logical processors", Environment.ProcessorCount.ToString());
                break;

            case Kind.Memory:
                x.Subtitle.Text = s is null ? "" : $"{Gb(s.MemoryTotal)} usable";
                x.Big.Text = s is null ? "—" : Gb(s.MemoryUsed);
                x.Caption.Text = s is null ? "In use" : $"In use of {Gb(s.MemoryTotal)} ({Percent(s.MemoryUsed, s.MemoryTotal):0}%)";
                Fact(x, "In use", s is null ? "—" : Gb(s.MemoryUsed));
                Fact(x, "Available", s is null ? "—" : Gb(Math.Max(0, s.MemoryTotal - s.MemoryUsed)));
                Fact(x, "Committed", s is null ? "—" : $"{Gb(s.Committed)} / {Gb(s.CommitLimit)}");
                Fact(x, "Total", s is null ? "—" : Gb(s.MemoryTotal));
                break;

            case Kind.Disk:
                x.Subtitle.Text = "All disks";
                x.Big.Text = s is null ? "—" : $"{s.DiskActive:0}%";
                x.Caption.Text = "Active time";
                Fact(x, "Active time", s is null ? "—" : $"{s.DiskActive:0}%");
                Fact(x, "Read", s is null ? "—" : ByteRate(s.DiskRead));
                Fact(x, "Write", s is null ? "—" : ByteRate(s.DiskWrite));
                break;

            case Kind.Network:
                ReadAdaptersNowAndThen();
                x.Subtitle.Text = _adapterDetail;
                x.Big.Text = s is null ? "—" : $"↓ {BitRate(s.NetDown)}";
                x.Caption.Text = s is null ? "Receiving" : $"Receiving · ↑ {BitRate(s.NetUp)} sending";
                Fact(x, "Receive", s is null ? "—" : BitRate(s.NetDown));
                Fact(x, "Send", s is null ? "—" : BitRate(s.NetUp));
                Fact(x, "Connected", _adapters);
                break;

            case Kind.Gpu:
                x.Subtitle.Text = Hardware.GpuName;
                if (s?.Gpu is double g)
                {
                    x.Big.Text = $"{g:0}%";
                    x.Caption.Text = "Utilisation (busiest engine)";
                }
                else
                {
                    x.Big.FontSize = 36;
                    x.Big.Text = s is null ? "—" : "Not available";
                    x.Caption.Text = "Windows isn't reporting GPU use";
                }
                Fact(x, "Utilisation", s?.Gpu is double u ? $"{u:0}%" : "Not available");
                Fact(x, "Adapter", Hardware.GpuNames);
                Fact(x, "Dedicated memory", Hardware.GpuMemory > 0 ? Gb(Hardware.GpuMemory) : "—");
                break;
        }
    }

    static void Fact(Section x, string label, string value)
    {
        if (x.Facts.TryGetValue(label, out var text)) text.Text = value;
    }

    /// <summary>Which adapters are up. Asking Windows takes a few milliseconds, so it's done at most every 10 s, and only while Network is showing.</summary>
    void ReadAdaptersNowAndThen()
    {
        var now = Environment.TickCount64;
        if (_adaptersAt >= 0 && now - _adaptersAt < 10_000) return;
        _adaptersAt = now;
        try
        {
            var up = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .ToList();
            _adapters = up.Count == 0 ? "Not connected" : string.Join(", ", up.Select(n => n.Name));
            _adapterDetail = up.Count == 0 ? "" : up[0].Description;
        }
        catch
        {
            _adapters = "—";
            _adapterDetail = "";
        }
    }

    // ---------- selection ----------

    void Select(Section x)
    {
        _selected = x;
        foreach (var s in _sections)
        {
            s.Detail.Visibility = s == x ? Visibility.Visible : Visibility.Collapsed;
            Look(s);
        }
        Refresh(x);
    }

    /// <summary>Card colours: raised when picked or under the mouse, with an accent edge on the picked one.</summary>
    void Look(Section s)
    {
        var picked = s == _selected;
        var hover = s.Card.IsMouseOver;
        s.Card.SetResourceReference(Border.BackgroundProperty,
            picked && hover ? "RaisedHoverBrush" : picked || hover ? "RaisedBrush" : "CardBrush");
        s.Card.SetResourceReference(Border.BorderBrushProperty,
            picked ? "AccentBrush" : s.Card.IsKeyboardFocused ? "TextDimBrush" : "EdgeBrush");
    }

    void Move(Section from, int by)
    {
        var i = Math.Clamp(_sections.IndexOf(from) + by, 0, _sections.Count - 1);
        _sections[i].Card.Focus();
        Select(_sections[i]);
    }

    // ---------- building ----------

    Section Add(Kind kind, string name, string glyph, string[] facts)
    {
        var s = new Section(kind, name);
        BuildCard(s, glyph);
        BuildDetail(s, facts);
        _sections.Add(s);
        return s;
    }

    void BuildCard(Section s, string glyph)
    {
        var icon = Text(13, "TextDimBrush", "IconFont");
        icon.Text = glyph;
        icon.VerticalAlignment = VerticalAlignment.Center;
        var name = Text(13, "TextDimBrush");
        name.Text = s.Name;
        name.Margin = new Thickness(8, 0, 0, 0);
        var header = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, name } };

        s.CardValue = Text(22, "TextBrush", "DisplayFont", FontWeights.SemiBold, tabular: true);
        s.CardValue.Margin = new Thickness(0, 6, 0, 0);
        s.CardSub = Text(12, "TextDimBrush");
        s.CardSub.Margin = new Thickness(0, 2, 0, 0);

        var left = new StackPanel { Children = { header, s.CardValue, s.CardSub } };

        s.Spark = new HistoryGraph { Width = 92, Height = 44, ShowGrid = false, Thickness = 1.5, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 0, 2) };
        SetColumn(s.Spark, 1);

        var grid = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } },
            Children = { left, s.Spark }
        };

        var card = new Border
        {
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(0, 0, 0, 10),
            BorderThickness = new Thickness(1.5),
            Cursor = Cursors.Hand,
            Focusable = true,
            FocusVisualStyle = null,
            Child = grid
        };
        System.Windows.Automation.AutomationProperties.SetName(card, s.Name);
        card.MouseEnter += (_, _) => Look(s);
        card.MouseLeave += (_, _) => Look(s);
        card.MouseLeftButtonDown += (_, e) =>
        {
            card.Focus();
            Select(s);
            e.Handled = true;
        };
        card.GotKeyboardFocus += (_, _) => { if (_selected != s) Select(s); else Look(s); };
        card.LostKeyboardFocus += (_, _) => Look(s);
        card.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Up: Move(s, -1); e.Handled = true; break;
                case Key.Down: Move(s, 1); e.Handled = true; break;
                case Key.Enter or Key.Space: Select(s); e.Handled = true; break;
            }
        };
        s.Card = card;
    }

    void BuildDetail(Section s, string[] facts)
    {
        // Row 0: title and the hardware's name
        var title = Text(28, "TextBrush", "DisplayFont", FontWeights.SemiBold);
        title.Text = s.Name;
        s.Subtitle = Text(14, "TextDimBrush");
        s.Subtitle.HorizontalAlignment = HorizontalAlignment.Right;
        s.Subtitle.VerticalAlignment = VerticalAlignment.Center;
        s.Subtitle.Margin = new Thickness(16, 4, 0, 0);
        SetColumn(s.Subtitle, 1);
        var header = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) } },
            Children = { title, s.Subtitle }
        };

        // Row 1: the big number
        s.Big = Text(56, "TextBrush", "DisplayFont", FontWeights.SemiBold, tabular: true);
        s.Big.Margin = new Thickness(0, 10, 0, 0);
        s.Caption = Text(14, "TextDimBrush");
        s.Caption.Margin = new Thickness(2, -4, 0, 0);
        var now = new StackPanel { Children = { s.Big, s.Caption } };
        SetRow(now, 1);

        // Row 2: the minute of history, with its scale in the corner
        s.Graph = new HistoryGraph { Thickness = 2 };
        s.ScaleTop = Text(11, "TextDimBrush");
        s.ScaleTop.HorizontalAlignment = HorizontalAlignment.Right;
        s.ScaleTop.VerticalAlignment = VerticalAlignment.Top;
        s.ScaleTop.Margin = new Thickness(0, 2, 4, 0);
        var plot = new Grid { MinHeight = 120, Margin = new Thickness(0, 18, 0, 0), Children = { s.Graph, s.ScaleTop } };
        SetRow(plot, 2);

        // Row 3: time axis (and, for two series, a legend added later)
        var seconds = Text(11, "TextDimBrush");
        seconds.Text = $"{s.Graph.Capacity} seconds";
        var zero = Text(11, "TextDimBrush");
        zero.Text = "0";
        zero.HorizontalAlignment = HorizontalAlignment.Right;
        SetColumn(zero, 2);
        var axis = new Grid
        {
            Margin = new Thickness(0, 6, 0, 0),
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            Children = { seconds, zero }
        };
        SetRow(axis, 3);
        s.Axis = axis;

        // Row 4: detail tiles
        var tiles = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        foreach (var label in facts)
        {
            var name = Text(12, "TextDimBrush");
            name.Text = label;
            var value = Text(18, "TextBrush", "DisplayFont", FontWeights.SemiBold, tabular: true);
            value.Margin = new Thickness(0, 2, 0, 0);
            value.TextWrapping = TextWrapping.Wrap;
            value.MaxWidth = 340;
            value.Text = "—";
            var tile = new Border
            {
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(14, 10, 14, 12),
                Margin = new Thickness(0, 0, 10, 10),
                MinWidth = 132,
                Child = new StackPanel { Children = { name, value } }
            };
            tile.SetResourceReference(Border.BackgroundProperty, "RaisedBrush");
            tiles.Children.Add(tile);
            s.Facts[label] = value;
        }
        SetRow(tiles, 4);

        s.Detail = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto }
            },
            Children = { header, now, plot, axis, tiles },
            Visibility = Visibility.Collapsed
        };
    }

    /// <summary>A key for graphs with two lines, in the middle of the time axis.</summary>
    static void AddLegend(Section s, string first, string second)
    {
        var legend = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        legend.Children.Add(Dot("AccentBrush"));
        legend.Children.Add(LegendText(first));
        legend.Children.Add(Dot("TextDimBrush"));
        legend.Children.Add(LegendText(second));
        SetColumn(legend, 1);
        s.Axis.Children.Add(legend);

        static Ellipse Dot(string key)
        {
            var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 6, 0) };
            dot.SetResourceReference(Shape.FillProperty, key);
            return dot;
        }

        static TextBlock LegendText(string text)
        {
            var t = Text(11, "TextDimBrush");
            t.Text = text;
            return t;
        }
    }

    /// <summary>A themed text block. Numbers that change every second use tabular digits so they don't jiggle.</summary>
    static TextBlock Text(double size, string brush = "TextBrush", string font = "UiFont", FontWeight? weight = null, bool tabular = false)
    {
        var t = new TextBlock { FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        t.SetResourceReference(TextBlock.FontFamilyProperty, font);
        if (tabular) System.Windows.Documents.Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
        return t;
    }

    // ---------- formatting ----------

    static double Percent(long part, long whole) => whole <= 0 ? 0 : 100.0 * part / whole;

    static string Gb(long bytes) => $"{bytes / 1_073_741_824.0:0.0} GB";

    static string ByteRate(double b) => b switch
    {
        >= 1_073_741_824 => $"{b / 1_073_741_824:0.00} GB/s",
        >= 1_048_576 => $"{b / 1_048_576:0.0} MB/s",
        _ => $"{b / 1024:0} KB/s"
    };

    static string BitRate(double bytesPerSecond)
    {
        var b = bytesPerSecond * 8;
        return b >= 1e9 ? $"{b / 1e9:0.00} Gbps" : b >= 1e6 ? $"{b / 1e6:0.0} Mbps" : $"{b / 1e3:0} Kbps";
    }

    static string Uptime()
    {
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var clock = $"{up.Hours:00}:{up.Minutes:00}:{up.Seconds:00}";
        return up.TotalDays >= 1 ? $"{(int)up.TotalDays}d {clock}" : clock;
    }

    // ---------- hardware names ----------

    /// <summary>Names and fixed numbers of the CPU and GPU, read from the registry once per run (they don't change).</summary>
    static class Hardware
    {
        static bool _read;
        static string _cpuName = "", _gpuName = "", _gpuNames = "";
        static string? _speed;
        static long _gpuMemory;

        public static string CpuName { get { Read(); return _cpuName; } }
        /// <summary>Base clock, e.g. "3.60 GHz"; null if Windows doesn't say.</summary>
        public static string? Speed { get { Read(); return _speed; } }
        /// <summary>The main GPU (the first real one, not Microsoft's software adapters).</summary>
        public static string GpuName { get { Read(); return _gpuName; } }
        /// <summary>Every present GPU, for machines with integrated plus discrete graphics.</summary>
        public static string GpuNames { get { Read(); return _gpuNames; } }
        public static long GpuMemory { get { Read(); return _gpuMemory; } }

        static void Read()
        {
            if (_read) return;
            _read = true;

            try
            {
                using var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                _cpuName = (cpu?.GetValue("ProcessorNameString") as string)?.Trim() ?? "";
                if (cpu?.GetValue("~MHz") is int mhz && mhz > 0) _speed = mhz >= 1000 ? $"{mhz / 1000.0:0.00} GHz" : $"{mhz} MHz";
            }
            catch
            {
                // no access; the panel just leaves the name out
            }

            // Driver names and memory sizes from the display adapter class key (stale entries for
            // removed cards can linger there, so the list of present adapters comes from
            // EnumDisplayDevices when it answers).
            var registry = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var adapters = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                foreach (var sub in adapters?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;
                    try
                    {
                        using var key = adapters!.OpenSubKey(sub);
                        if ((key?.GetValue("DriverDesc") as string)?.Trim() is not { Length: > 0 } desc) continue;
                        var size = Math.Max(Size(key!.GetValue("HardwareInformation.qwMemorySize")), Size(key.GetValue("HardwareInformation.MemorySize")));
                        registry[desc] = Math.Max(registry.GetValueOrDefault(desc), size);
                    }
                    catch
                    {
                        // a key we may not read
                    }
                }
            }
            catch
            {
                // no access
            }

            var present = PresentAdapters();
            var names = present.Count > 0 ? present : registry.Keys.ToList();
            var real = names.Where(n => !n.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase)).ToList();
            if (real.Count > 0) names = real;

            _gpuName = names.FirstOrDefault() ?? "";
            _gpuNames = names.Count == 0 ? "—" : string.Join(", ", names);
            _gpuMemory = names.Select(n => registry.GetValueOrDefault(n)).DefaultIfEmpty(0L).Max();
        }

        static long Size(object? value) => value switch
        {
            long l => l,
            int i => (uint)i,
            byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
            byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
            _ => 0
        };

        static List<string> PresentAdapters()
        {
            var names = new List<string>();
            try
            {
                var device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                for (uint i = 0; EnumDisplayDevices(null, i, ref device, 0); i++)
                {
                    var name = device.DeviceString?.Trim();
                    if ((device.StateFlags & DISPLAY_DEVICE_MIRRORING_DRIVER) == 0 && !string.IsNullOrEmpty(name)
                        && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                        names.Add(name);
                    device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                }
            }
            catch
            {
                // fall back to the registry list
            }
            return names;
        }

        const int DISPLAY_DEVICE_MIRRORING_DRIVER = 0x8;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplayDevices(string? device, uint index, ref DISPLAY_DEVICE info, uint flags);
    }
}
