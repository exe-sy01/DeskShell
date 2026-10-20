using System.Diagnostics;
using Microsoft.Win32;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;

namespace DeskShell.Services;

/// <summary>Wi-Fi and Bluetooth radios, plus the Windows light/dark theme.</summary>
public sealed class SystemToggles
{
    const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public Radio? WiFi { get; private set; }
    public Radio? Bluetooth { get; private set; }

    /// <summary>Raised on a background thread when a radio is switched on or off by anyone.</summary>
    public event Action? RadiosChanged;

    public async Task StartAsync()
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed)
            {
                Log.Info("Windows denied access to the radios");
                return;
            }

            foreach (var radio in await Radio.GetRadiosAsync())
            {
                if (radio.Kind == RadioKind.WiFi && WiFi == null) WiFi = radio;
                if (radio.Kind == RadioKind.Bluetooth && Bluetooth == null) Bluetooth = radio;
            }

            if (WiFi != null) WiFi.StateChanged += (_, _) => RadiosChanged?.Invoke();
            if (Bluetooth != null) Bluetooth.StateChanged += (_, _) => RadiosChanged?.Invoke();
            RadiosChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("Could not read the radios", ex);
        }
    }

    public static async Task SetAsync(Radio radio, bool on)
    {
        try
        {
            var result = await radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
            if (result != RadioAccessStatus.Allowed) Log.Info($"Switching {radio.Kind} was refused: {result}");
        }
        catch (Exception ex)
        {
            Log.Error($"Could not switch {radio.Kind}", ex);
        }
    }

    /// <summary>"HomeNet", "Ethernet", or "Not connected".</summary>
    public static string ConnectionName()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile == null) return "Not connected";
            return profile.IsWlanConnectionProfile ? profile.ProfileName : profile.IsWwanConnectionProfile ? "Mobile" : "Ethernet";
        }
        catch
        {
            return "";
        }
    }

    public static bool IsDarkMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    public static void SetDarkMode(bool dark)
    {
        using var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey);
        key.SetValue("AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
        key.SetValue("SystemUsesLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
        Native.BroadcastThemeChange();
    }

    public static void OpenSettings(string page)
    {
        try { Process.Start(new ProcessStartInfo($"ms-settings:{page}") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error($"Could not open settings page {page}", ex); }
    }
}
