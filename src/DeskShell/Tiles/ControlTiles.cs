using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DeskShell.Services;

namespace DeskShell.Tiles;

/// <summary>Icon button + slider + percentage, used by volume and brightness.</summary>
public abstract class SliderTile : TileBase
{
    protected readonly Button MainButton;
    protected readonly TextBlock IconGlyph;
    protected readonly Slider Slider;
    protected readonly TextBlock Label;
    protected readonly TextBlock Heading;
    bool _updating;

    protected SliderTile(ShellContext c, string glyph, string heading) : base(c)
    {
        Padding = new Thickness(8, 6, 14, 8);
        IconGlyph = Icon(glyph);
        MainButton = new Button { Style = Res<Style>("GhostButton"), Content = IconGlyph };
        MainButton.Click += (_, _) => OnIconClicked();
        Slider = new Slider { Style = Res<Style>("FlatSlider"), Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        Slider.ValueChanged += (_, e) =>
        {
            if (!_updating) OnSlid((int)Math.Round(e.NewValue));
        };
        Label = Text("", 12.5, dim: true);
        Label.Width = 44;
        Label.TextAlignment = TextAlignment.Right;
        Heading = Text(heading, 12, dim: true);
        Heading.Margin = new Thickness(8, 0, 0, 0);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(Slider, 1);
        Grid.SetColumn(Label, 2);
        row.Children.Add(MainButton);
        row.Children.Add(Slider);
        row.Children.Add(Label);

        var stack = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(Heading, Dock.Top);
        stack.Children.Add(Heading);
        stack.Children.Add(row);
        row.VerticalAlignment = VerticalAlignment.Center;
        Child = stack;
    }

    public override (int W, int H)[] Sizes => [(2, 1), (4, 1)];

    protected void SetValue(int? value, string label)
    {
        _updating = true;
        Slider.IsEnabled = MainButton.IsEnabled = value != null;
        Slider.Value = value ?? 0;
        Label.Text = label;
        _updating = false;
    }

    protected abstract void OnSlid(int value);
    protected virtual void OnIconClicked() { }
}

public sealed class VolumeTile : SliderTile
{
    public VolumeTile(ShellContext c) : base(c, "", "Volume") { }

    public override string Id => "volume";
    public override string Title => "Volume";

    public override void OnOpened()
    {
        var volume = AudioService.GetVolume();
        var muted = AudioService.IsMuted();
        SetValue(volume, volume == null ? "—" : muted ? "Muted" : $"{volume}%");
        IconGlyph.Text = muted || volume == 0 ? "" : "";
    }

    protected override void OnSlid(int value)
    {
        AudioService.SetVolume(value);
        if (value > 0 && AudioService.IsMuted()) AudioService.SetMuted(false);
        Label.Text = $"{value}%";
        IconGlyph.Text = value == 0 ? "" : "";
    }

    protected override void OnIconClicked()
    {
        AudioService.SetMuted(!AudioService.IsMuted());
        OnOpened();
    }
}

public sealed class BrightnessTile : SliderTile
{
    readonly DispatcherTimer _debounce;
    int _pending;

    public BrightnessTile(ShellContext c) : base(c, "", "Brightness")
    {
        // DDC/CI is slow; send the value once the slider settles.
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await BrightnessService.SetAsync(_pending);
        };
    }

    public override string Id => "brightness";
    public override string Title => "Brightness";

    public override async void OnOpened()
    {
        Label.Text = "…";
        var value = await BrightnessService.GetAsync();
        SetValue(value, value == null ? "" : $"{value}%");
        Heading.Text = value == null ? "Brightness (your monitors don't allow it)" : "Brightness";
    }

    protected override void OnSlid(int value)
    {
        Label.Text = $"{value}%";
        _pending = value;
        _debounce.Stop();
        _debounce.Start();
    }

    protected override void OnIconClicked()
    {
        CloseShell();
        SystemToggles.OpenSettings("display");
    }
}

public sealed class MediaTile : TileBase
{
    readonly Border _art = new() { Width = 50, Height = 50, CornerRadius = new CornerRadius(9) };
    readonly TextBlock _title = Text("", 13.5, bold: true);
    readonly TextBlock _artist = Text("", 12.5, dim: true);
    readonly TextBlock _play = Icon("", 18);
    readonly StackPanel _controls;
    readonly TextBlock _empty = Text("Nothing playing", 13, dim: true);

    public MediaTile(ShellContext c) : base(c)
    {
        Padding = new Thickness(10, 8, 8, 8);
        _art.Background = Res<Brush>("TileHoverBrush");

        var text = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_title);
        text.Children.Add(_artist);

        var play = new Button { Style = Res<Style>("GhostButton"), Width = 40, Height = 40, Content = _play, ToolTip = "Play / pause" };
        play.Click += async (_, _) => await Context.Media.PlayPauseAsync();
        _controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _controls.Children.Add(IconButton("", "Previous", async (_, _) => await Context.Media.PreviousAsync(), 34));
        _controls.Children.Add(play);
        _controls.Children.Add(IconButton("", "Next", async (_, _) => await Context.Media.NextAsync(), 34));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(_controls, 2);
        Grid.SetColumn(_empty, 1);
        _empty.Margin = new Thickness(12, 0, 0, 0);
        grid.Children.Add(_art);
        grid.Children.Add(text);
        grid.Children.Add(_controls);
        grid.Children.Add(_empty);
        Child = grid;

        c.Media.Changed += () => Dispatcher.InvokeAsync(OnOpened);
    }

    public override string Id => "media";
    public override string Title => "Now playing";
    public override (int W, int H)[] Sizes => [(2, 1), (4, 1)];

    public override async void OnOpened()
    {
        var now = await Context.Media.GetAsync();
        var has = now != null;
        _empty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        _controls.Visibility = _title.Visibility = _artist.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (now == null)
        {
            _art.Background = Res<Brush>("TileHoverBrush");
            _art.Child = Icon("", 18);
            return;
        }
        _title.Text = now.Title;
        _artist.Text = now.Artist;
        _play.Text = now.IsPlaying ? "" : "";
        _art.Child = now.Art == null ? Icon("", 18) : null;
        _art.Background = now.Art != null ? new ImageBrush(now.Art) { Stretch = Stretch.UniformToFill } : Res<Brush>("TileHoverBrush");
    }
}

public sealed class AudioOutputTile : TileBase
{
    readonly TextBlock _device = Text("", 13, bold: true);

    public AudioOutputTile(ShellContext c) : base(c)
    {
        Cursor = System.Windows.Input.Cursors.Hand;
        var icon = Icon("", 18);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        stack.Children.Add(Text("Sound output", 12, dim: true));
        stack.Children.Add(_device);
        var chevron = Icon("", 11);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(stack, 1);
        Grid.SetColumn(chevron, 2);
        grid.Children.Add(icon);
        grid.Children.Add(stack);
        grid.Children.Add(chevron);
        Child = grid;

        MouseLeftButtonUp += (_, _) => ShowDevices();
        MouseEnter += (_, _) => Background = Res<Brush>("TileHoverBrush");
        MouseLeave += (_, _) => Background = Res<Brush>("TileBrush");
    }

    public override string Id => "audioout";
    public override string Title => "Sound output";
    public override (int W, int H)[] Sizes => [(2, 1), (1, 1), (4, 1)];

    public override void OnOpened()
    {
        var current = AudioService.GetOutputDevices().FirstOrDefault(d => d.IsDefault);
        _device.Text = current?.Name ?? "No output device";
    }

    void ShowDevices()
    {
        var menu = new ContextMenu { PlacementTarget = this, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var device in AudioService.GetOutputDevices())
        {
            var item = new MenuItem { Header = (device.IsDefault ? "✓  " : "     ") + device.Name };
            var id = device.Id;
            item.Click += (_, _) =>
            {
                AudioService.SetDefaultOutput(id);
                OnOpened();
            };
            menu.Items.Add(item);
        }
        var settings = new MenuItem { Header = "     Sound settings…" };
        settings.Click += (_, _) =>
        {
            CloseShell();
            SystemToggles.OpenSettings("sound");
        };
        menu.Items.Add(settings);
        menu.IsOpen = true;
    }
}

public sealed class StatsTile : TileBase
{
    readonly StatsService _stats = new();
    readonly DispatcherTimer _timer;
    readonly (TextBlock Value, Border Bar)[] _rows = new (TextBlock, Border)[3];
    readonly TextBlock _ramDetail = Text("", 11, dim: true);

    public StatsTile(ShellContext c) : base(c)
    {
        var grid = new Grid();
        string[] names = ["CPU", "Memory", "GPU"];
        for (var i = 0; i < 3; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var col = new StackPanel { Margin = new Thickness(i == 0 ? 0 : 10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            col.Children.Add(Text(names[i], 11.5, dim: true));
            var value = Text("—", 17, bold: true);
            value.FontFamily = Res<FontFamily>("DisplayFont");
            col.Children.Add(value);
            var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Res<Brush>("TileHoverBrush"), Margin = new Thickness(0, 4, 0, 0) };
            var bar = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Res<Brush>("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            track.Child = bar;
            col.Children.Add(track);
            Grid.SetColumn(col, i);
            grid.Children.Add(col);
            _rows[i] = (value, bar);
        }
        Child = grid;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Update(), Dispatcher);
        _timer.Stop();
    }

    public override string Id => "stats";
    public override string Title => "CPU / memory / GPU";
    public override (int W, int H)[] Sizes => [(2, 1), (4, 1)];

    public override void OnOpened()
    {
        Update();
        _timer.Start();
    }

    public override void OnClosed() => _timer.Stop();

    void Update()
    {
        var s = _stats.Sample();
        Set(0, s.Cpu, $"{s.Cpu:0}%");
        Set(1, s.Ram, $"{s.RamUsedGb:0.0} / {s.RamTotalGb:0} GB");
        if (s.Gpu is { } gpu) Set(2, gpu, $"{gpu:0}%");
        else _rows[2].Value.Text = "—";
    }

    void Set(int i, double percent, string text)
    {
        _rows[i].Value.Text = text;
        var track = (Border)_rows[i].Bar.Parent;
        _rows[i].Bar.Width = Math.Max(0, track.ActualWidth * Math.Clamp(percent, 0, 100) / 100);
        _rows[i].Bar.Background = Res<Brush>(percent > 85 ? "DangerBrush" : "AccentBrush");
    }
}

public sealed class WeatherTile : TileBase
{
    readonly TextBlock _glyph = Icon("", 20);
    readonly TextBlock _temp = Text("—", 22, bold: true);
    readonly TextBlock _line1 = Text("", 12, dim: true);
    readonly TextBlock _line2 = Text("", 12, dim: true);

    public WeatherTile(ShellContext c) : base(c)
    {
        _glyph.HorizontalAlignment = HorizontalAlignment.Left;
        _temp.FontFamily = Res<FontFamily>("DisplayFont");
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(_glyph);
        _temp.Margin = new Thickness(8, -2, 0, 0);
        top.Children.Add(_temp);
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(top);
        stack.Children.Add(_line1);
        stack.Children.Add(_line2);
        Child = stack;
        ToolTip = "Right-click to set your city";
        MouseRightButtonUp += (_, _) =>
        {
            CloseShell();
            App.Instance.ShowSettings();
        };
    }

    public override string Id => "weather";
    public override string Title => "Weather";

    public override async void OnOpened()
    {
        var (weather, problem) = await WeatherService.GetAsync(App.Settings.WeatherCity, App.Settings.Fahrenheit);
        if (weather == null)
        {
            _temp.Text = "—";
            _line1.Text = problem ?? "";
            _line2.Text = "";
            return;
        }
        _glyph.Text = weather.Glyph;
        _temp.Text = $"{weather.Temperature:0}°";
        _line1.Text = weather.Condition;
        _line2.Text = $"{weather.Place} · {weather.High:0}° / {weather.Low:0}°";
    }
}
