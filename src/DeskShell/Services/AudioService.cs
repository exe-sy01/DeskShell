using System.Runtime.InteropServices;

namespace DeskShell.Services;

public sealed record AudioDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// One app's sound on one output device (all its audio sessions there). Its volume applies before
/// the sound reaches the device, so it works alongside Voicemeeter.
/// </summary>
public sealed class AppAudio
{
    internal readonly List<object> Sessions = new(); // ISimpleAudioVolume objects

    public required uint ProcessId { get; init; }
    public required string Name { get; init; }
    public string? ExePath { get; init; }
    public bool IsSystemSounds { get; init; }
    public float Volume { get; set; }
    public bool Muted { get; set; }
}

/// <summary>An output device and the apps playing through it.</summary>
public sealed class OutputAudio
{
    internal object? Endpoint; // IAudioEndpointVolume

    /// <summary>Short name, e.g. "VAIO", "AUX" or "Speakers".</summary>
    public required string Name { get; init; }
    public float Volume { get; set; }
    public bool Muted { get; set; }
    public List<AppAudio> Apps { get; } = new();
}

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

    // ---------- per-app volume (audio sessions) ----------

    /// <summary>Every output device with apps making (or ready to make) sound, and those apps.</summary>
    public static List<OutputAudio> GetOutputsWithApps()
    {
        var outputs = new Dictionary<string, OutputAudio>();
        try
        {
            var enumerator = NewEnumerator();
            if (enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out var devices) != 0 || devices == null) return new();
            devices.GetCount(out var deviceCount);
            var managerIid = typeof(IAudioSessionManager2).GUID;
            var endpointIid = typeof(IAudioEndpointVolume).GUID;
            for (uint d = 0; d < deviceCount; d++)
            {
                if (devices.Item(d, out var device) != 0 || device == null) continue;
                if (device.Activate(ref managerIid, CLSCTX_ALL, IntPtr.Zero, out var managerObj) != 0 || managerObj is not IAudioSessionManager2 manager) continue;
                if (manager.GetSessionEnumerator(out var sessions) != 0 || sessions == null) continue;
                var name = ShortDeviceName(ReadName(device)) ?? "Output";
                if (!outputs.TryGetValue(name, out var output))
                {
                    output = new OutputAudio { Name = name };
                    if (device.Activate(ref endpointIid, CLSCTX_ALL, IntPtr.Zero, out var ep) == 0 && ep is IAudioEndpointVolume endpoint)
                    {
                        output.Endpoint = endpoint;
                        if (endpoint.GetMasterVolumeLevelScalar(out var level) == 0) output.Volume = level;
                        output.Muted = endpoint.GetMute(out var muted) == 0 && muted;
                    }
                    outputs[name] = output;
                }

                // One row per app: apps like Discord play through several processes
                var apps = output.Apps.ToDictionary(a => a.IsSystemSounds ? "system" : a.ExePath?.ToLowerInvariant() ?? $"pid {a.ProcessId}");
                sessions.GetCount(out var count);
                for (var i = 0; i < count; i++)
                {
                    if (sessions.GetSession(i, out var session) != 0 || session == null) continue;
                    if (session.GetState(out var state) != 0 || state == 2) continue; // 2 = expired
                    session.GetProcessId(out var pid);
                    var isSystem = session.IsSystemSoundsSession() == 0;
                    if (session is not ISimpleAudioVolume volume) continue;

                    var exe = isSystem ? null : ProcessPath(pid);
                    var key = isSystem ? "system" : exe?.ToLowerInvariant() ?? $"pid {pid}";
                    if (!apps.TryGetValue(key, out var app))
                    {
                        app = new AppAudio
                        {
                            ProcessId = pid,
                            ExePath = exe,
                            IsSystemSounds = isSystem,
                            Name = isSystem ? "System sounds" : AppNameFor(exe, pid)
                        };
                        volume.GetMasterVolume(out var level);
                        volume.GetMute(out var muted);
                        app.Volume = level;
                        app.Muted = muted;
                        apps[key] = app;
                        output.Apps.Add(app);
                    }
                    app.Sessions.Add(volume);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not list app audio", ex);
        }
        foreach (var output in outputs.Values)
            output.Apps.Sort((a, b) => a.IsSystemSounds != b.IsSystemSounds ? a.IsSystemSounds.CompareTo(b.IsSystemSounds) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return outputs.Values.Where(o => o.Apps.Count > 0).OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void SetOutputVolume(OutputAudio output, float level)
    {
        output.Volume = Math.Clamp(level, 0f, 1f);
        try { (output.Endpoint as IAudioEndpointVolume)?.SetMasterVolumeLevelScalar(output.Volume, ref _context); } catch { }
    }

    public static void SetOutputMuted(OutputAudio output, bool muted)
    {
        output.Muted = muted;
        try { (output.Endpoint as IAudioEndpointVolume)?.SetMute(muted, ref _context); } catch { }
    }

    public static void SetAppVolume(AppAudio app, float level)
    {
        app.Volume = Math.Clamp(level, 0f, 1f);
        foreach (var s in app.Sessions.OfType<ISimpleAudioVolume>())
            try { s.SetMasterVolume(app.Volume, ref _context); } catch { }
    }

    public static void SetAppMuted(AppAudio app, bool muted)
    {
        app.Muted = muted;
        foreach (var s in app.Sessions.OfType<ISimpleAudioVolume>())
            try { s.SetMute(muted, ref _context); } catch { }
    }

    /// <summary>"Voicemeeter AUX Input (VB-Audio Voicemeeter VAIO)" → "AUX"; "Speakers (Realtek…)" → "Speakers".</summary>
    static string? ShortDeviceName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (name.Contains("VAIO3", StringComparison.OrdinalIgnoreCase)) return "VAIO3";
        if (name.Contains("AUX", StringComparison.OrdinalIgnoreCase)) return "AUX";
        if (name.StartsWith("Voicemeeter Input", StringComparison.OrdinalIgnoreCase)) return "VAIO";
        var paren = name.IndexOf(" (", StringComparison.Ordinal);
        return paren > 0 ? name[..paren] : name;
    }

    static string AppNameFor(string? exe, uint pid)
    {
        if (exe != null)
        {
            try
            {
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
                var product = info.ProductName?.Trim();
                if (!string.IsNullOrEmpty(product) && product.Length <= 32 && !product.Contains("Operating System")) return product;
                if (!string.IsNullOrWhiteSpace(info.FileDescription) && info.FileDescription.Length <= 40) return info.FileDescription.Trim();
            }
            catch { }
            return System.IO.Path.GetFileNameWithoutExtension(exe);
        }
        try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch { return $"App {pid}"; }
    }

    static string? ProcessPath(uint pid)
    {
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new System.Text.StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr groupingParam, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr groupingParam, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, IntPtr context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, IntPtr context);
        [PreserveSig] int GetGroupingParam(out Guid param);
        [PreserveSig] int SetGroupingParam(IntPtr param, IntPtr context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid context);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
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
