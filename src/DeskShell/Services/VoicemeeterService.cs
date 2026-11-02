using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DeskShell.Services;

/// <summary>A Voicemeeter virtual input (a Windows playback device such as VAIO) or bus (output).</summary>
/// <remarks>Key is the default name (VAIO, AUX, VAIO3, A1, B1…); Name is the user's label if they set one.</remarks>
public sealed record VmChannel(string Param, string Key, string Name, bool IsBus, float Gain, bool Muted);

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

    public static List<VmChannel> Channels()
    {
        lock (Gate) return ReadChannels();
    }

    static List<VmChannel> ReadChannels()
    {
        var list = new List<VmChannel>();
        if (!IsAvailable) return list;
        _dirty?.Invoke(); // refreshes Voicemeeter's parameter snapshot
        var (strips, buses) = TypeNow() switch
        {
            1 => (3, 2),
            2 => (5, 5),
            _ => (8, 8)
        };
        // Only the virtual strips: they're the playback devices Windows apps send sound to
        // (VAIO, AUX, VAIO3). The hardware strips are microphones and line-ins.
        var virtualStart = TypeNow() switch { 1 => 2, 2 => 3, _ => 5 };
        string[] virtualNames = ["VAIO", "AUX", "VAIO3"];
        for (var i = virtualStart; i < strips; i++)
        {
            var param = $"Strip[{i}]";
            var key = virtualNames[i - virtualStart];
            list.Add(new VmChannel(param, key, Label(param, key), false, Float($"{param}.Gain"), Float($"{param}.Mute") > 0.5f));
        }
        var hardwareBuses = TypeNow() switch { 1 => 1, 2 => 3, _ => 5 };
        for (var i = 0; i < buses; i++)
        {
            var param = $"Bus[{i}]";
            // Skip hardware outputs with no device picked in Voicemeeter
            if (i < hardwareBuses && String($"{param}.device.name") is "") continue;
            var fallback = i < hardwareBuses ? $"A{i + 1}" : $"B{i - hardwareBuses + 1}";
            list.Add(new VmChannel(param, fallback, Label(param, fallback), true, Float($"{param}.Gain"), Float($"{param}.Mute") > 0.5f));
        }
        return list;
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
