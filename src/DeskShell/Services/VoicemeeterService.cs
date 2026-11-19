using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DeskShell.Services;

/// <summary>A Voicemeeter virtual input (a Windows playback device such as VAIO) or bus (output).</summary>
/// <remarks>Key is the default name (VAIO, AUX, VAIO3, A1, B1…); Name is the user's label if they set one.</remarks>
public sealed record VmChannel(string Param, string Key, string Name, bool IsBus, float Gain, bool Muted)
{
    /// <summary>A hardware input (microphone, line-in) rather than a virtual one.</summary>
    public bool Hardware { get; init; }
    /// <summary>Its number among the strips or buses (for level meters).</summary>
    public int Index { get; init; }
    /// <summary>For inputs: which outputs (A1, B1…) it's sent to.</summary>
    public IReadOnlyList<(string Bus, bool On)> Routes { get; init; } = [];
}

/// <summary>
/// Controls Voicemeeter through its official remote API (VoicemeeterRemote64.dll, installed with
/// Voicemeeter), the same way Voicemeeter's own add-ons and Stream Deck plugins do. Only used if
/// Voicemeeter is installed; does nothing otherwise.
/// </summary>
public static class VoicemeeterService
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int NoArgs();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetTypeFn(out int type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetFloat([MarshalAs(UnmanagedType.LPStr)] string name, out float value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetFloat([MarshalAs(UnmanagedType.LPStr)] string name, float value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetStringW([MarshalAs(UnmanagedType.LPStr)] string name, IntPtr buffer);

    static IntPtr _dll;
    static NoArgs? _login, _logout, _dirty;
    static GetTypeFn? _type;
    static GetFloat? _getFloat;
    static SetFloat? _setFloat;
    static GetStringW? _getString;
    static bool _loggedIn, _triedLoad;
    // The mixer reads on a background thread and writes from another; one call at a time
    static readonly object Gate = new();

    public const float MinGain = -60, MaxGain = 12;

    /// <summary>Voicemeeter is installed and running, and we're connected to it.</summary>
    public static bool IsAvailable
    {
        get { lock (Gate) return Connect() && TypeNow() > 0; }
    }

    static bool Connect()
    {
        if (_loggedIn) return true;
        if (_triedLoad && _dll == IntPtr.Zero) return false;
        _triedLoad = true;
        try
        {
            var path = DllPath();
            if (path == null || !NativeLibrary.TryLoad(path, out _dll)) return false;
            T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_dll, name));
            _login = Export<NoArgs>("VBVMR_Login");
            _logout = Export<NoArgs>("VBVMR_Logout");
            _dirty = Export<NoArgs>("VBVMR_IsParametersDirty");
            _type = Export<GetTypeFn>("VBVMR_GetVoicemeeterType");
            _getFloat = Export<GetFloat>("VBVMR_GetParameterFloat");
            _setFloat = Export<SetFloat>("VBVMR_SetParameterFloat");
            _getString = Export<GetStringW>("VBVMR_GetParameterStringW");
            var result = _login();
            _loggedIn = result >= 0; // 0 = connected, 1 = connected but Voicemeeter isn't running yet
            Log.Info($"Voicemeeter remote: login {result}");
            return _loggedIn;
        }
        catch (Exception ex)
        {
            Log.Error("Could not load Voicemeeter's remote API", ex);
            return false;
        }
    }

    static string? DllPath()
    {
        foreach (var root in new[] { @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{root}\VB:Voicemeeter {{17359A74-1236-5467}}");
            if (key?.GetValue("UninstallString") is string uninstall)
            {
                var dll = Path.Combine(Path.GetDirectoryName(uninstall.Trim('"'))!, "VoicemeeterRemote64.dll");
                if (File.Exists(dll)) return dll;
            }
        }
        var fallback = @"C:\Program Files (x86)\VB\Voicemeeter\VoicemeeterRemote64.dll";
        return File.Exists(fallback) ? fallback : null;
    }

    /// <summary>1 = Voicemeeter, 2 = Banana, 3 = Potato; 0 when it isn't running.</summary>
    static int TypeNow()
    {
        try { return _type != null && _type(out var t) == 0 ? t : 0; }
        catch { return 0; }
    }

    /// <summary>
    /// The virtual inputs (VAIO, AUX, VAIO3) and the outputs in use. With <paramref name="everything"/>, also the
    /// hardware inputs that have a device, and each input's routing to the outputs.
    /// </summary>
    public static List<VmChannel> Channels(bool everything = false)
    {
        lock (Gate) return ReadChannels(everything);
    }

    /// <summary>"Voicemeeter", "Voicemeeter Banana" or "Voicemeeter Potato"; null when it isn't running.</summary>
    public static string? Edition
    {
        get
        {
            lock (Gate)
                return !Connect() ? null : TypeNow() switch { 1 => "Voicemeeter", 2 => "Voicemeeter Banana", 3 => "Voicemeeter Potato", _ => null };
        }
    }

    static (int Strips, int HardwareStrips, int Buses, int HardwareBuses) Layout() => TypeNow() switch
    {
        1 => (3, 2, 2, 1),
        2 => (5, 3, 5, 3),
        _ => (8, 5, 8, 5)
    };

    static string BusKey(int i, int hardwareBuses) => i < hardwareBuses ? $"A{i + 1}" : $"B{i - hardwareBuses + 1}";

    static List<VmChannel> ReadChannels(bool everything)
    {
        var list = new List<VmChannel>();
        if (!IsAvailable) return list;
        _dirty?.Invoke(); // refreshes Voicemeeter's parameter snapshot
        var (strips, virtualStart, buses, hardwareBuses) = Layout();
        var busKeys = Enumerable.Range(0, buses).Select(i => BusKey(i, hardwareBuses)).ToList();

        IReadOnlyList<(string, bool)> Routes(string param) =>
            everything ? busKeys.Select(b => (b, Float($"{param}.{b}") > 0.5f)).ToList() : [];

        // The hardware strips are microphones and line-ins: only on the Voicemeeter tab, and only ones with a device.
        if (everything)
            for (var i = 0; i < virtualStart; i++)
            {
                var param = $"Strip[{i}]";
                if (String($"{param}.device.name") is "") continue;
                list.Add(new VmChannel(param, $"IN{i + 1}", Label(param, $"Input {i + 1}"), false, Float($"{param}.Gain"), Float($"{param}.Mute") > 0.5f)
                    { Hardware = true, Index = i, Routes = Routes(param) });
            }
        // The virtual strips: the playback devices Windows apps send sound to (VAIO, AUX, VAIO3).
        string[] virtualNames = ["VAIO", "AUX", "VAIO3"];
        for (var i = virtualStart; i < strips; i++)
        {
            var param = $"Strip[{i}]";
            var key = virtualNames[i - virtualStart];
            list.Add(new VmChannel(param, key, Label(param, key), false, Float($"{param}.Gain"), Float($"{param}.Mute") > 0.5f)
                { Index = i, Routes = Routes(param) });
        }
        for (var i = 0; i < buses; i++)
        {
            var param = $"Bus[{i}]";
            // Skip hardware outputs with no device picked in Voicemeeter
            if (i < hardwareBuses && String($"{param}.device.name") is "") continue;
            var fallback = busKeys[i];
            list.Add(new VmChannel(param, fallback, Label(param, fallback), true, Float($"{param}.Gain"), Float($"{param}.Mute") > 0.5f)
                { Hardware = i < hardwareBuses, Index = i });
        }
        return list;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetLevelFn(int type, int channel, out float value);
    static GetLevelFn? _getLevel;

    /// <summary>
    /// How loud a strip (after its fader) or bus is right now, as an amplitude (1 = full scale): the louder of
    /// its first two channels. Cheap: Voicemeeter keeps these in shared memory.
    /// </summary>
    public static float Level(VmChannel channel)
    {
        lock (Gate)
        {
            if (!_loggedIn || _dll == IntPtr.Zero) return 0;
            try
            {
                _getLevel ??= Marshal.GetDelegateForFunctionPointer<GetLevelFn>(NativeLibrary.GetExport(_dll, "VBVMR_GetLevel"));
                var (_, hardwareStrips, _, _) = Layout();
                // Hardware strips have 2 channels each, virtual strips and buses 8.
                var (type, first) = channel.IsBus ? (3, channel.Index * 8)
                    : channel.Index < hardwareStrips ? (1, channel.Index * 2)
                    : (1, hardwareStrips * 2 + (channel.Index - hardwareStrips) * 8);
                var left = _getLevel(type, first, out var l) == 0 ? l : 0;
                var right = _getLevel(type, first + 1, out var r) == 0 ? r : 0;
                return Math.Max(left, right);
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>Sends an input to an output (A1, B1…), or stops sending it.</summary>
    public static void SetRoute(VmChannel channel, string bus, bool on)
    {
        try { lock (Gate) _setFloat?.Invoke($"{channel.Param}.{bus}", on ? 1f : 0f); }
        catch (Exception ex) { Log.Error("Voicemeeter routing change failed", ex); }
    }

    /// <summary>Restarts Voicemeeter's audio engine (the fix for crackling or a device that went missing).</summary>
    public static void RestartEngine()
    {
        try { lock (Gate) _setFloat?.Invoke("Command.Restart", 1f); }
        catch (Exception ex) { Log.Error("Voicemeeter engine restart failed", ex); }
    }

    /// <summary>Voicemeeter is installed (whether or not it's running).</summary>
    public static bool Installed => DllPath() != null;

    /// <summary>Starts Voicemeeter, or brings its window up if it's running (it only runs once).</summary>
    public static bool Open()
    {
        var folder = DllPath() is { } dll ? Path.GetDirectoryName(dll) : null;
        if (folder == null) return false;
        // The richest edition installed (they all install together), matching the one running if any.
        var running = TypeNow();
        string[] potato = ["voicemeeter8x64.exe", "voicemeeter8.exe"], banana = ["voicemeeterpro_x64.exe", "voicemeeterpro.exe"], basic = ["voicemeeter_x64.exe", "voicemeeter.exe"];
        var order = running switch { 1 => basic, 2 => banana, 3 => potato, _ => [.. potato, .. banana, .. basic] };
        foreach (var name in order)
        {
            var exe = Path.Combine(folder, name);
            if (!File.Exists(exe)) continue;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Couldn't start Voicemeeter", ex);
                return false;
            }
        }
        return false;
    }

    static float Float(string name)
    {
        try { return _getFloat != null && _getFloat(name, out var v) == 0 ? v : 0; }
        catch { return 0; }
    }

    static string Label(string param, string fallback) => String($"{param}.Label") is { Length: > 0 } label ? label : fallback;

    /// <summary>A string parameter, or null if Voicemeeter can't read it.</summary>
    static string? String(string name)
    {
        if (_getString == null) return null;
        var buffer = Marshal.AllocHGlobal(1024);
        try
        {
            if (_getString(name, buffer) != 0) return null;
            return Marshal.PtrToStringUni(buffer)?.Trim() ?? "";
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static void SetGain(VmChannel channel, float db)
    {
        try { lock (Gate) _setFloat?.Invoke($"{channel.Param}.Gain", Math.Clamp(db, MinGain, MaxGain)); }
        catch (Exception ex) { Log.Error("Voicemeeter gain change failed", ex); }
    }

    public static void SetMuted(VmChannel channel, bool muted)
    {
        try { lock (Gate) _setFloat?.Invoke($"{channel.Param}.Mute", muted ? 1f : 0f); }
        catch (Exception ex) { Log.Error("Voicemeeter mute change failed", ex); }
    }

    public static void Disconnect()
    {
        lock (Gate)
        {
            if (!_loggedIn) return;
            try { _logout?.Invoke(); } catch { }
            _loggedIn = false;
        }
    }
}
