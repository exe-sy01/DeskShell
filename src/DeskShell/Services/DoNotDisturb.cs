using System.Runtime.InteropServices;

namespace DeskShell.Services;

/// <summary>
/// Windows' Do Not Disturb switch, through the same QuietHoursSettings COM object the Settings
/// page uses (undocumented, so every call is guarded). Do Not Disturb is the "Priority only"
/// quiet-hours profile; off is "Unrestricted". Settings and the notification centre update live.
/// </summary>
internal static class DoNotDisturb
{
    const string OnProfile = "Microsoft.QuietHoursProfile.PriorityOnly";
    const string OffProfile = "Microsoft.QuietHoursProfile.Unrestricted";
    static readonly Guid SettingsClsid = new("f53321fa-34f8-4b7f-b9a3-361877cb94cf");
    static bool? _supported;

    public static bool IsSupported => _supported ??= Profile() != null;

    /// <summary>True for any quiet-hours profile other than Unrestricted (Windows' "alarms only" counts too).</summary>
    public static bool IsOn => Profile() is { } profile && profile != OffProfile;

    public static bool SetOn(bool on)
    {
        try
        {
            var hr = Settings().put_UserSelectedProfile(on ? OnProfile : OffProfile);
            if (hr != 0) Log.Info($"Do Not Disturb: switching {(on ? "on" : "off")} returned 0x{hr:X8}");
            return hr == 0;
        }
        catch (Exception ex)
        {
            Log.Error("Could not switch Do Not Disturb", ex);
            return false;
        }
    }

    static string? Profile()
    {
        try
        {
            return Settings().get_UserSelectedProfile(out var profile) == 0 ? profile : null;
        }
        catch (Exception ex)
        {
            Log.Error("Do Not Disturb isn't available", ex);
            return null;
        }
    }

    static IQuietHoursSettings Settings() =>
        (IQuietHoursSettings)Activator.CreateInstance(Type.GetTypeFromCLSID(SettingsClsid, throwOnError: true)!)!;

    [ComImport, Guid("6bff4732-81ec-4ffb-ae67-b6c1bc29631f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IQuietHoursSettings
    {
        [PreserveSig] int get_UserSelectedProfile([MarshalAs(UnmanagedType.LPWStr)] out string profileId);
        [PreserveSig] int put_UserSelectedProfile([MarshalAs(UnmanagedType.LPWStr)] string profileId);
    }
}
