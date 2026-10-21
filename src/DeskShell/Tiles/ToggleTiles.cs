using System.Diagnostics;
using DeskShell.Services;
using Windows.Devices.Radios;

namespace DeskShell.Tiles;

public sealed class WifiTile : ToggleTile
{
    public WifiTile(ShellContext c) : base(c, "", "Wi-Fi")
    {
        c.Toggles.RadiosChanged += () => Dispatcher.InvokeAsync(OnOpened);
    }

    public override string Id => "wifi";
    public override string Title => "Wi-Fi / network";
    protected override string SettingsPage => Context.Toggles.WiFi != null ? "network-wifi" : "network";

    public override void OnOpened()
    {
        if (Context.Toggles.WiFi is { } wifi)
        {
            var on = wifi.State == RadioState.On;
            Button.Content = "Wi-Fi";
            Button.Tag = "";
            Show(on, on ? SystemToggles.ConnectionName() : "Off");
        }
        else
        {
            Button.Content = "Network";
            Button.Tag = "";
            Show(false, SystemToggles.ConnectionName());
        }
    }

    protected override async void OnToggled(bool on)
    {
        if (Context.Toggles.WiFi is { } wifi)
        {
            await SystemToggles.SetAsync(wifi, on);
        }
        else
        {
            CloseShell();
            SystemToggles.OpenSettings("network");
        }
        OnOpened();
    }
}

public sealed class BluetoothTile : ToggleTile
{
    public BluetoothTile(ShellContext c) : base(c, "", "Bluetooth")
    {
        c.Toggles.RadiosChanged += () => Dispatcher.InvokeAsync(OnOpened);
    }

    public override string Id => "bluetooth";
    public override string Title => "Bluetooth";
    protected override string SettingsPage => "bluetooth";

    public override void OnOpened()
    {
        var bt = Context.Toggles.Bluetooth;
        if (bt == null) Show(false, "Not available", enabled: false);
        else Show(bt.State == RadioState.On, bt.State == RadioState.On ? "On" : "Off");
    }

    protected override async void OnToggled(bool on)
    {
        if (Context.Toggles.Bluetooth is { } bt) await SystemToggles.SetAsync(bt, on);
        OnOpened();
    }
}

public sealed class DarkModeTile : ToggleTile
{
    public DarkModeTile(ShellContext c) : base(c, "", "Dark mode") { }

    public override string Id => "darkmode";
    public override string Title => "Dark mode";
    protected override string SettingsPage => "colors";

    public override void OnOpened()
    {
        var dark = SystemToggles.IsDarkMode();
        Show(dark, dark ? "On" : "Off");
    }

    protected override void OnToggled(bool on)
    {
        SystemToggles.SetDarkMode(on);
        OnOpened();
    }
}

public sealed class FocusTile : ToggleTile
{
    public FocusTile(ShellContext c) : base(c, "", "Do not disturb")
    {
    }

    public override string Id => "focus";
    public override string Title => "Do not disturb";
    protected override string SettingsPage => "notifications";

    public override void OnOpened()
    {
        if (!DoNotDisturb.IsSupported)
        {
            Show(false, "Open settings");
            return;
        }
        var on = DoNotDisturb.IsOn;
        Show(on, on ? "On" : "Off");
    }

    protected override void OnToggled(bool on)
    {
        if (!DoNotDisturb.IsSupported)
        {
            CloseShell();
            SystemToggles.OpenSettings("notifications");
            return;
        }
        DoNotDisturb.SetOn(on);
        OnOpened();
    }
}

public sealed class ScreenshotTile : ToggleTile
{
    public ScreenshotTile(ShellContext c) : base(c, "", "Screenshot") { }

    public override string Id => "screenshot";
    public override string Title => "Screenshot";
    public override (int W, int H)[] Sizes => [(1, 1)];

    public override void OnOpened() => Show(false, "Snip an area");

    protected override async void OnToggled(bool on)
    {
        Button.IsChecked = false;
        CloseShell();
        await Task.Delay(250); // let the shell fade before the snip freezes the screen
        try { Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Could not start the snipping tool", ex); }
    }
}

public sealed class LayoutsTile : ToggleTile
{
    public LayoutsTile(ShellContext c) : base(c, "", "Window layout") { }

    public override string Id => "layouts";
    public override string Title => "Window layout";

    public override void OnOpened() => Show(false, App.Instance.Zones.LayoutNameAtCursor());

    protected override void OnToggled(bool on)
    {
        Button.IsChecked = false;
        App.Instance.Zones.CycleLayoutAtCursor();
        OnOpened();
    }
}
