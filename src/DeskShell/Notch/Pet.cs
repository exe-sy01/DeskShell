using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DeskShell.Notch;

/// <summary>What the pet is feeling, mostly driven by what Claude is doing.</summary>
public enum PetMood
{
    /// <summary>Nothing going on: blinks now and then, eyes follow the mouse.</summary>
    Idle,
    /// <summary>Claude is working: eyes scan side to side.</summary>
    Working,
    /// <summary>Claude needs an answer from you: wide eyes and a little "!".</summary>
    NeedsYou,
    /// <summary>A task finished: happy ^ ^ eyes and a hop.</summary>
    Happy,
    /// <summary>Nothing for a long while: half-closed eyes.</summary>
    Sleepy
}

/// <summary>
/// DeskShell's pet: a soft blob with a dark visor and two pixel eyes, in the theme's accent colour.
/// It's drawn in code and redraws only when something changes — a blink, the eyes moving, a
/// reaction — never continuously, so it costs next to nothing while it sits in the notch.
/// Poke it and it squints; poke it three times quickly and it gets dizzy.
/// </summary>
public sealed class Pet : FrameworkElement
{
    PetMood _mood = PetMood.Idle;
    readonly DispatcherTimer _blink, _frames, _watch;
    readonly Random _random = new();
    double _lid;          // 0 open … 1 shut
    double _hop;          // 0 … 1 … 0 while hopping
    double _look;         // −1 … 1, where the eyes look sideways
    double _lookY;        // −1 … 1 up/down
    double _scan;         // phase while working
    int _frame, _burstFrames;
    Action? _burst;
    readonly List<long> _pokes = new();
    bool _dizzy, _squint;
    double _spin;

    public Pet()
    {
        Width = 26;
        Height = 22;
        Cursor = Cursors.Hand;
        ToolTip = "Your DeskShell pet";
        _blink = new DispatcherTimer(DispatcherPriority.Background) { Interval = NextBlink() };
        _blink.Tick += (_, _) => { _blink.Interval = NextBlink(); if (_mood is PetMood.Idle or PetMood.Working or PetMood.Sleepy) Blink(); };
        // Short bursts of frames for blinks, hops and moods that move.
        _frames = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Render, (_, _) => Step(), Dispatcher);
        _frames.Stop();
        // Eyes follow the mouse, a few times a second, and only redraw when where they look changes.
        _watch = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => Watch(), Dispatcher);
        IsVisibleChanged += (_, _) => SetRunning(IsVisible);
        MouseLeftButtonUp += (_, e) => { Poke(); e.Handled = true; };
    }

    /// <summary>How big it draws (it scales from its 26×22 design size).</summary>
    public double Scale { get => Width / 26; set { Width = 26 * value; Height = 22 * value; InvalidateVisual(); } }

    public PetMood Mood
    {
        get => _mood;
        set
        {
            if (_mood == value) return;
            var before = _mood;
            _mood = value;
            if (value == PetMood.Happy) Hop();
            // Only "working" keeps moving (slowly); every other mood is a still picture between blinks.
            if (value == PetMood.Working) StartFrames(int.MaxValue, null, slow: true);
            else if (before == PetMood.Working) StopFrames();
            InvalidateVisual();
        }
    }

    void SetRunning(bool on)
    {
        if (on)
        {
            _blink.Start();
            _watch.Start();
            if (_mood == PetMood.Working) StartFrames(int.MaxValue, null, slow: true);
        }
        else
        {
            _blink.Stop();
            _watch.Stop();
            _frames.Stop();
        }
    }

    TimeSpan NextBlink() => TimeSpan.FromMilliseconds(2800 + _random.Next(4200));

    void Blink() => StartFrames(6, () => { _lid = 0; });

    /// <summary>A happy hop (when a task finishes).</summary>
    public void Hop() => StartFrames(14, () => { _hop = 0; });

    void Poke()
    {
        var now = Environment.TickCount64;
        _pokes.RemoveAll(t => now - t > 900);
        _pokes.Add(now);
        if (_pokes.Count >= 3)
        {
            _pokes.Clear();
            _dizzy = true;
            _spin = 0;
            StartFrames(40, () => { _dizzy = false; });
            return;
        }
        _squint = true;
        StartFrames(12, () => { _squint = false; });
    }

    void StartFrames(int count, Action? done, bool slow = false)
    {
        _burst?.Invoke(); // finish whatever was playing
        // Blinks and hops are smooth (25 a second) but short; the working scan is slow (8 a second).
        _frames.Interval = TimeSpan.FromMilliseconds(slow ? 125 : 40);
        _frame = 0;
        _burstFrames = count;
        _burst = done;
        if (!_frames.IsEnabled && IsVisible) _frames.Start();
    }

    void StopFrames()
    {
        _frames.Stop();
        _burst?.Invoke();
        _burst = null;
        InvalidateVisual();
    }

    void Step()
    {
        _frame++;
        // Blink: shut over 3 frames, open over 3
        if (_burstFrames == 6) _lid = _frame <= 3 ? _frame / 3.0 : Math.Max(0, (6 - _frame) / 3.0);
        if (_burstFrames == 14) _hop = Math.Sin(Math.PI * Math.Min(1, _frame / 14.0));
        if (_dizzy) _spin += 0.5;
        if (_mood == PetMood.Working) _scan += 0.5;
        InvalidateVisual();
        if (_frame >= _burstFrames)
        {
            _frames.Stop();
            var done = _burst;
            _burst = null;
            done?.Invoke();
            if (_mood == PetMood.Working) StartFrames(int.MaxValue, null, slow: true);
            InvalidateVisual();
        }
    }

    void Watch()
    {
        if (_mood == PetMood.Working || _dizzy || !IsVisible) return;
        try
        {
            var mouse = PointFromScreen(GetCursor());
            var dx = mouse.X - ActualWidth / 2;
            var dy = mouse.Y - ActualHeight / 2;
            // Only when the mouse is nearby (a few hundred pixels); otherwise look ahead.
            var far = Math.Sqrt(dx * dx + dy * dy) > 700;
            var look = far ? 0 : Math.Round(Math.Clamp(dx / 250, -1, 1) * 4) / 4;
            var lookY = far ? 0 : Math.Round(Math.Clamp(dy / 250, -1, 1) * 2) / 2;
            if (look == _look && lookY == _lookY) return;
            _look = look;
            _lookY = lookY;
            InvalidateVisual();
        }
        catch (InvalidOperationException)
        {
            // not connected to a window yet
        }
    }

    static Point GetCursor()
    {
        Native.GetCursorPos(out var p);
        return new Point(p.X, p.Y);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var s = ActualWidth / 26;
        if (s <= 0) return;
        var accent = ThemeManager.Current.Accent;
        var visorColour = Color.FromRgb(0x12, 0x12, 0x16);
        var eyeColour = Color.FromRgb(0xEF, 0xFF, 0xF6);

        dc.PushTransform(new ScaleTransform(s, s));
        // Breathing isn't animated (that would redraw all the time); the hop lifts the whole pet.
        dc.PushTransform(new TranslateTransform(0, -3 * _hop));

        // Body: a soft blob, a little wider at the bottom
        var body = new StreamGeometry();
        using (var c = body.Open())
        {
            c.BeginFigure(new Point(13, 1.5), true, true);
            c.BezierTo(new Point(21, 1.5), new Point(24.5, 6), new Point(24.5, 12.5), true, true);
            c.BezierTo(new Point(24.5, 18.5), new Point(20.5, 21), new Point(13, 21), true, true);
            c.BezierTo(new Point(5.5, 21), new Point(1.5, 18.5), new Point(1.5, 12.5), true, true);
            c.BezierTo(new Point(1.5, 6), new Point(5, 1.5), new Point(13, 1.5), true, true);
        }
        body.Freeze();
        var light = Color.FromArgb(255, (byte)Math.Min(255, accent.R + 40), (byte)Math.Min(255, accent.G + 40), (byte)Math.Min(255, accent.B + 40));
        var fill = new LinearGradientBrush(light, accent, 90);
        dc.DrawGeometry(fill, null, body);

        // Visor
        var visor = new Rect(4.5, 6, 17, 9);
        dc.DrawRoundedRectangle(new SolidColorBrush(visorColour), null, visor, 4.5, 4.5);
        // A glint on the visor
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)), null, new Rect(6, 7, 4, 1.4), 0.7, 0.7);

        // Eyes
        var eye = new SolidColorBrush(_mood == PetMood.NeedsYou ? Color.FromRgb(0xFF, 0xD2, 0x4A) : eyeColour);
        var lookX = _mood == PetMood.Working ? Math.Sin(_scan) * 2.2 : _look * 2.2;
        var lookY = _mood == PetMood.Working ? 0 : _lookY * 1.2;
        var cx1 = 9.5 + lookX;
        var cx2 = 16.5 + lookX;
        var cy = 10.5 + lookY;
        if (_dizzy)
        {
            // Spinning x eyes
            foreach (var cx in new[] { cx1, cx2 })
            {
                dc.PushTransform(new RotateTransform(_spin * 40, cx, cy));
                var pen = new Pen(eye, 1.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.DrawLine(pen, new Point(cx - 1.6, cy - 1.6), new Point(cx + 1.6, cy + 1.6));
                dc.DrawLine(pen, new Point(cx - 1.6, cy + 1.6), new Point(cx + 1.6, cy - 1.6));
                dc.Pop();
            }
        }
        else if (_mood == PetMood.Happy || _squint)
        {
            // ^ ^ (happy) or > < (poked)
            var pen = new Pen(eye, 1.3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            foreach (var (cx, dir) in new[] { (cx1, 1.0), (cx2, -1.0) })
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    if (_squint)
                    {
                        c.BeginFigure(new Point(cx - 1.4 * dir, cy - 1.5), false, false);
                        c.LineTo(new Point(cx + 1.2 * dir, cy), true, true);
                        c.LineTo(new Point(cx - 1.4 * dir, cy + 1.5), true, true);
                    }
                    else
                    {
                        c.BeginFigure(new Point(cx - 1.8, cy + 0.8), false, false);
                        c.LineTo(new Point(cx, cy - 1.2), true, true);
                        c.LineTo(new Point(cx + 1.8, cy + 0.8), true, true);
                    }
                }
                g.Freeze();
                dc.DrawGeometry(null, pen, g);
            }
        }
        else
        {
            // Pixel eyes: rounded squares that shut from the top when blinking or sleepy
            var lid = _mood == PetMood.Sleepy ? Math.Max(_lid, 0.6) : _lid;
            var size = _mood == PetMood.NeedsYou ? 3.4 : 2.8;
            var h = Math.Max(0.5, size * (1 - lid));
            foreach (var cx in new[] { cx1, cx2 })
                dc.DrawRoundedRectangle(eye, null, new Rect(cx - size / 2, cy - h / 2 + (size - h) / 2, size, h), 0.8, 0.8);
        }

        // Moods with a little sign above the head
        if (_mood == PetMood.NeedsYou)
        {
            var sign = new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x4A));
            dc.DrawRoundedRectangle(sign, null, new Rect(22, 0, 1.6, 3.6), 0.8, 0.8);
            dc.DrawEllipse(sign, null, new Point(22.8, 5), 0.9, 0.9);
        }
        dc.Pop();
        dc.Pop();
    }
}
