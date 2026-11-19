using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeskShell.Services;

namespace DeskShell.Notch;

/// <summary>
/// The notch's Voicemeeter tab: a small mixing desk. Every input (microphones, VAIO, AUX…) and output
/// (A1, B1…) as a fader with a live level meter and mute, and for inputs, which outputs they go to.
/// Changes made in Voicemeeter itself show up here within a couple of seconds.
/// </summary>
sealed class VoicemeeterPage(NotchContext c) : NotchPage(c)
{
    public override string Id => "voicemeeter";
    public override string Title => "Voicemeeter";
    public override string Glyph => "";

    static readonly Brush MutedRed = UI.Frozen(Color.FromRgb(0xE0, 0x3C, 0x3C));

    readonly TextBlock _edition = UI.Label("", 15, bold: true, font: UI.Display);
    readonly TextBlock _status = UI.Label("", 12, brush: UI.Dim);
    readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _desk = new() { Orientation = Orientation.Horizontal };
    readonly List<Column> _columns = [];
    DockPanel? _root;
    DispatcherTimer? _meters;
    string _layout = "";
    int _reads;
    Task _writes = Task.CompletedTask;

    sealed class Column
    {
        public required VmChannel Channel;
        public required Fader Fader;
        public required TextBlock Db;
        public required Border MuteBg;
        public required TextBlock MuteGlyph;
        public readonly List<(string Bus, Border Chip, TextBlock Text)> Chips = [];
    }

    protected override UIElement Build()
    {
        var words = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(_edition);
        _status.Margin = new Thickness(12, 3, 0, 0);
        words.Children.Add(_status);
        var head = new DockPanel { Margin = new Thickness(2, 0, 0, 8) };
        DockPanel.SetDock(_actions, Dock.Right);
        head.Children.Add(_actions);
        head.Children.Add(words);

        var scroller = new ScrollViewer
        {
            Content = _desk,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        // The wheel scrolls sideways, except over a fader (where it moves the fader).
        scroller.PreviewMouseWheel += (_, e) =>
        {
            if (e.OriginalSource is Fader) return;
            scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - e.Delta / 2.0);
            e.Handled = true;
        };

        _root = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        _root.Children.Add(head);
        _root.Children.Add(scroller);
        // Meters only move while the tab is on screen.
        _root.IsVisibleChanged += (_, _) =>
        {
            if (_root.IsVisible && _columns.Count > 0) _meters?.Start();
            else _meters?.Stop();
        };
        return _root;
    }

    /// <summary>Changes go to Voicemeeter off the UI thread, in order (a fader sends many).</summary>
    void Write(Action change) => _writes = _writes.ContinueWith(_ =>
    {
        try { change(); }
        catch (Exception ex) { Log.Error("Voicemeeter change failed", ex); }
    }, TaskScheduler.Default);

    public override void Refresh() => _ = LoadAsync();
    public override void Tick() => _ = LoadAsync();

    async Task LoadAsync()
    {
        var read = ++_reads;
        string? edition;
        List<VmChannel> channels;
        try
        {
            (edition, channels) = await Task.Run(() => (VoicemeeterService.Edition, VoicemeeterService.Channels(everything: true)));
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't read Voicemeeter", ex);
            return;
        }
        if (read != _reads) return;

        _actions.Children.Clear();
        if (edition == null || channels.Count == 0)
        {
            _edition.Text = "Voicemeeter";
            _status.Text = VoicemeeterService.Installed ? "isn't running" : "isn't installed";
            if (VoicemeeterService.Installed) _actions.Children.Add(UI.Pill("Start Voicemeeter", () => { if (!VoicemeeterService.Open()) _status.Text = "couldn't be started"; else _status.Text = "starting…"; }, ""));
            _desk.Children.Clear();
            _columns.Clear();
            _layout = "";
            _meters?.Stop();
            return;
        }
        _edition.Text = edition;
        var inputs = channels.Count(ch => !ch.IsBus);
        _status.Text = $"{inputs} inputs · {channels.Count - inputs} outputs";
        _actions.Children.Add(UI.Pill("Restart audio engine", () => Write(VoicemeeterService.RestartEngine), ""));
        _actions.Children.Add(UI.Pill("Open Voicemeeter", () => { Context.Collapse(); VoicemeeterService.Open(); }, ""));

        // Rebuilt only when channels come, go or are renamed; otherwise just brought up to date.
        var layout = string.Join("|", channels.Select(ch => $"{ch.Param}:{ch.Name}:{ch.Routes.Count}"));
        if (layout != _layout)
        {
            _layout = layout;
            BuildDesk(channels);
        }
        else
        {
            for (var i = 0; i < _columns.Count && i < channels.Count; i++) Show(_columns[i], channels[i]);
        }
        if (_root?.IsVisible == true) StartMeters();
    }

    void BuildDesk(List<VmChannel> channels)
    {
        _desk.Children.Clear();
        _columns.Clear();
        var groups = new[] { ("INPUTS", channels.Where(ch => !ch.IsBus).ToList()), ("OUTPUTS", channels.Where(ch => ch.IsBus).ToList()) };
        foreach (var (title, list) in groups)
        {
            if (list.Count == 0) continue;
            var group = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
            var label = UI.Label(title, 10.5, bold: true, brush: UI.Dim);
            label.Margin = new Thickness(8, 0, 0, 4);
            group.Children.Add(label);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var channel in list) row.Children.Add(BuildColumn(channel));
            group.Children.Add(row);
            _desk.Children.Add(group);
        }
    }

    UIElement BuildColumn(VmChannel channel)
    {
        var name = UI.Label(channel.Name, 12, bold: true);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.HorizontalAlignment = HorizontalAlignment.Center;
        name.MaxWidth = 80;
        name.ToolTip = channel.Name == channel.Key ? channel.Name : $"{channel.Name} ({channel.Key})";
        var db = UI.Label("", 11, brush: UI.Dim);
        db.HorizontalAlignment = HorizontalAlignment.Center;
        var fader = new Fader { Width = 44, Height = channel.IsBus ? 128 : 84, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 4) };

        var muteGlyph = UI.Glyph("", 12);
        var muteBg = new Border { Width = 30, Height = 24, CornerRadius = new CornerRadius(8), Background = UI.Raised, Child = muteGlyph };
        var mute = new Button { Content = muteBg, Template = UI.Bare(), Cursor = Cursors.Hand, Focusable = false, HorizontalAlignment = HorizontalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(mute, $"Mute {channel.Name}");

        var stack = new StackPanel { Width = channel.IsBus ? 80 : 96 };
        stack.Children.Add(name);
        stack.Children.Add(db);
        stack.Children.Add(fader);
        stack.Children.Add(mute);
        var column = new Column { Channel = channel, Fader = fader, Db = db, MuteBg = muteBg, MuteGlyph = muteGlyph };

        if (channel.Routes.Count > 0)
        {
            // Four to a row, so even Potato's eight outputs take two rows.
            var routes = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(2, 6, 0, 0), MaxWidth = 92 };
            foreach (var (bus, _) in channel.Routes)
            {
                var text = UI.Label(bus, 10, bold: true);
                text.HorizontalAlignment = HorizontalAlignment.Center;
                text.VerticalAlignment = VerticalAlignment.Center;
                var chip = new Border { Width = 21, Height = 17, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 2, 2), Child = text };
                var button = new Button { Content = chip, Template = UI.Bare(), Cursor = Cursors.Hand, Focusable = false };
                System.Windows.Automation.AutomationProperties.SetName(button, $"Send {channel.Name} to {bus}");
                var target = bus;
                button.Click += (_, _) =>
                {
                    var on = !column.Channel.Routes.FirstOrDefault(r => r.Bus == target).On;
                    column.Channel = column.Channel with { Routes = column.Channel.Routes.Select(r => r.Bus == target ? (r.Bus, on) : r).ToList() };
                    ShowRoutes(column);
                    var ch = column.Channel;
                    Write(() => VoicemeeterService.SetRoute(ch, target, on));
                };
                routes.Children.Add(button);
                column.Chips.Add((bus, chip, text));
            }
            stack.Children.Add(routes);
        }

        fader.Changed += value =>
        {
            column.Channel = column.Channel with { Gain = (float)value };
            column.Db.Text = Decibels(value);
            var ch = column.Channel;
            Write(() => VoicemeeterService.SetGain(ch, (float)value));
        };
        mute.Click += (_, _) =>
        {
            column.Channel = column.Channel with { Muted = !column.Channel.Muted };
            ShowMute(column);
            var ch = column.Channel;
            Write(() => VoicemeeterService.SetMuted(ch, ch.Muted));
        };
        Show(column, channel);
        _columns.Add(column);
        return stack;
    }

    static string Decibels(double db) => Math.Abs(db) < 0.05 ? "0 dB" : $"{db:+0.0;-0.0} dB";

    void Show(Column column, VmChannel channel)
    {
        // Don't fight a fader you're holding.
        if (column.Fader.Dragging) channel = channel with { Gain = column.Channel.Gain };
        column.Channel = channel;
        column.Fader.Fill = Context.Accent();
        column.Fader.Value = channel.Gain;
        column.Db.Text = Decibels(channel.Gain);
        ShowMute(column);
        ShowRoutes(column);
    }

    static void ShowMute(Column column)
    {
        var muted = column.Channel.Muted;
        column.MuteBg.Background = muted ? MutedRed : UI.Raised;
        column.MuteGlyph.Text = muted ? "" : "";
        column.Fader.Muted = muted;
    }

    void ShowRoutes(Column column)
    {
        foreach (var (bus, chip, text) in column.Chips)
        {
            var on = column.Channel.Routes.FirstOrDefault(r => r.Bus == bus).On;
            chip.Background = on ? Context.Accent() : UI.Raised;
            text.Foreground = on ? UI.Background : UI.Dim;
        }
    }

    void StartMeters()
    {
        _meters ??= new DispatcherTimer(TimeSpan.FromMilliseconds(66), DispatcherPriority.Render, (_, _) =>
        {
            foreach (var column in _columns) column.Fader.Level = VoicemeeterService.Level(column.Channel);
        }, Application.Current.Dispatcher);
        _meters.Start();
    }
}

/// <summary>A vertical fader from -60 to +12 dB with a level meter beside it. Drag, scroll, or double-click for 0 dB.</summary>
sealed class Fader : FrameworkElement
{
    const double Min = VoicemeeterService.MinGain, Max = VoicemeeterService.MaxGain, Pad = 9;
    static readonly Brush Green = UI.Frozen(Color.FromRgb(0x3B, 0xD1, 0x6F));
    static readonly Brush Amber = UI.Frozen(Color.FromRgb(0xF7, 0xA3, 0x25));
    static readonly Brush Red = UI.Frozen(Color.FromRgb(0xE0, 0x3C, 0x3C));

    double _value, _shown;
    bool _muted;

    public event Action<double>? Changed;
    public bool Dragging { get; private set; }
    public Brush Fill { get; set; } = Brushes.White;

    public double Value
    {
        get => _value;
        set
        {
            var v = Math.Clamp(value, Min, Max);
            if (Math.Abs(v - _value) < 0.01) return;
            _value = v;
            InvalidateVisual();
        }
    }

    public bool Muted
    {
        get => _muted;
        set { if (_muted == value) return; _muted = value; InvalidateVisual(); }
    }

    /// <summary>The meter, as an amplitude (1 = full scale); falls back gently rather than snapping.</summary>
    public double Level
    {
        set
        {
            var db = value <= 0.00001 ? -100 : 20 * Math.Log10(value);
            var fraction = Math.Clamp((db + 60) / 66, 0, 1);
            var next = Math.Max(fraction, _shown - 0.035);
            if (Math.Abs(next - _shown) < 0.003) return;
            _shown = next;
            InvalidateVisual();
        }
    }

    public Fader()
    {
        Cursor = Cursors.SizeNS;
        Focusable = false;
        ToolTip = "Drag or scroll · double-click for 0 dB";
    }

    double Track => Math.Max(1, ActualHeight - 2 * Pad);
    double Y(double v) => ActualHeight - Pad - (v - Min) / (Max - Min) * Track;

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // the whole box takes the mouse
        var x = ActualWidth / 2 - 4;
        var top = Pad;
        var bottom = ActualHeight - Pad;
        var y = Y(_value);

        dc.DrawRoundedRectangle(UI.Raised, null, new Rect(x - 3, top, 6, Track), 3, 3);
        dc.DrawRoundedRectangle(_muted ? UI.Faint : Fill, null, new Rect(x - 3, y, 6, Math.Max(0, bottom - y)), 3, 3);
        var zero = Y(0);
        dc.DrawRectangle(UI.Dim, null, new Rect(x - 10, zero - 0.5, 4, 1));

        var meterX = x + 11;
        dc.DrawRoundedRectangle(UI.Raised, null, new Rect(meterX, top, 4, Track), 2, 2);
        if (_shown > 0.001)
        {
            var meterY = bottom - _shown * Track;
            dc.DrawRoundedRectangle(_muted ? UI.Faint : _shown > 0.93 ? Red : _shown > 0.8 ? Amber : Green, null, new Rect(meterX, meterY, 4, bottom - meterY), 2, 2);
        }

        dc.DrawEllipse(UI.Text, null, new Point(x, y), 8, 8);
    }

    void SetFromMouse(MouseEventArgs e)
    {
        var y = e.GetPosition(this).Y;
        var v = Min + (ActualHeight - Pad - y) / Track * (Max - Min);
        v = Math.Round(v * 2) / 2;
        if (Math.Abs(v) < 0.75) v = 0; // 0 dB is easy to land on
        Set(v);
    }

    void Set(double v)
    {
        v = Math.Clamp(v, Min, Max);
        if (Math.Abs(v - _value) < 0.01) return;
        Value = v;
        Changed?.Invoke(_value);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            Set(0);
            return;
        }
        Dragging = true;
        CaptureMouse();
        SetFromMouse(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (Dragging && IsMouseCaptured) SetFromMouse(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!Dragging) return;
        Dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e) => Dragging = false;

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Set(_value + (e.Delta > 0 ? 1 : -1));
        e.Handled = true;
    }
}
