using System.Runtime.InteropServices;

namespace DeskShell.Services;

public sealed record AudioDevice(string Id, string Name, bool IsDefault);

/// <summary>Speaker volume and output-device switching through Windows Core Audio.</summary>
internal static class AudioService
{
    const int eRender = 0;
    const int eConsole = 0;
    const int eMultimedia = 1;
    const int eCommunications = 2;
    const int DEVICE_STATE_ACTIVE = 1;
    const int CLSCTX_ALL = 23;
    const ushort VT_LPWSTR = 31;
    static Guid _context = Guid.Empty;
    static PROPERTYKEY FriendlyName = new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };

    static IMMDeviceEnumerator NewEnumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();

    static IAudioEndpointVolume? GetEndpoint()
    {
        try
        {
            if (NewEnumerator().GetDefaultAudioEndpoint(eRender, eMultimedia, out var device) != 0 || device == null) return null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var o) != 0) return null;
            return o as IAudioEndpointVolume;
        }
        catch (Exception ex)
        {
            Log.Error("No default audio device", ex);
            return null;
        }
    }

    /// <summary>Volume 0-100, or null when there's no output device.</summary>
    public static int? GetVolume()
    {
        var ep = GetEndpoint();
        if (ep == null || ep.GetMasterVolumeLevelScalar(out var level) != 0) return null;
        return (int)Math.Round(level * 100);
    }

    public static void SetVolume(int percent) =>
        GetEndpoint()?.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, ref _context);

    public static bool IsMuted()
    {
        var ep = GetEndpoint();
        return ep != null && ep.GetMute(out var muted) == 0 && muted;
    }

    public static void SetMuted(bool muted) => GetEndpoint()?.SetMute(muted, ref _context);

    /// <summary>All active playback devices, default first.</summary>
    public static List<AudioDevice> GetOutputDevices()
    {
        var result = new List<AudioDevice>();
        try
        {
            var enumerator = NewEnumerator();
            string? defaultId = null;
            if (enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out var def) == 0 && def != null)
                def.GetId(out defaultId);

            if (enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out var collection) != 0 || collection == null) return result;
            collection.GetCount(out var count);
            for (uint i = 0; i < count; i++)
            {
                if (collection.Item(i, out var device) != 0 || device == null) continue;
                device.GetId(out var id);
                result.Add(new AudioDevice(id, ReadName(device) ?? id, id == defaultId));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not list audio devices", ex);
        }
        return result.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name).ToList();
    }

    /// <summary>Makes a device the default for everything (apps, calls, system sounds).</summary>
    public static bool SetDefaultOutput(string id)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            return policy.SetDefaultEndpoint(id, eConsole) == 0
                   & policy.SetDefaultEndpoint(id, eMultimedia) == 0
                   & policy.SetDefaultEndpoint(id, eCommunications) == 0;
        }
        catch (Exception ex)
        {
            Log.Error("Could not switch audio output", ex);
            return false;
        }
    }

    static string? ReadName(IMMDevice device)
    {
        if (device.OpenPropertyStore(0, out var store) != 0 || store == null) return null;
        var key = FriendlyName;
        if (store.GetValue(ref key, out var value) != 0) return null;
        try
        {
            return value.vt == VT_LPWSTR ? Marshal.PtrToStringUni(value.p) : null;
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    [DllImport("ole32.dll")]
    static extern int PropVariantClear(ref PROPVARIANT pvar);

    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPVARIANT
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public IntPtr p;
        public IntPtr p2;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class PolicyConfigClient { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    /// <summary>Undocumented but long-stable interface Windows' own sound settings use to change the default device.</summary>
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat(IntPtr a);
        [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
        [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility(IntPtr a, int b);
    }
}
