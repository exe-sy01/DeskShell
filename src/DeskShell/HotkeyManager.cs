using System.Windows.Interop;

namespace DeskShell;

/// <summary>Registers global hotkeys on a hidden message window.</summary>
internal sealed class HotkeyManager : IDisposable
{
    readonly HwndSource _source;
    readonly Dictionary<int, Action> _actions = new();
    int _nextId = 1;

    public HotkeyManager()
    {
        var parameters = new HwndSourceParameters("DeskShellHotkeys") { Width = 0, Height = 0, WindowStyle = 0 };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <returns>False if the text is invalid or another app already owns the combination.</returns>
    public bool Register(string hotkey, Action action)
    {
        if (!Settings.TryParseHotkey(hotkey, out var mods, out var vk))
        {
            Log.Error($"Hotkey \"{hotkey}\" is not valid");
            return false;
        }

        var id = _nextId++;
        if (!Native.RegisterHotKey(_source.Handle, id, mods | Native.MOD_NOREPEAT, vk))
        {
            Log.Error($"Hotkey \"{hotkey}\" is already used by another app");
            return false;
        }

        _actions[id] = action;
        Log.Info($"Registered hotkey {hotkey}");
        return true;
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            action();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) Native.UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
        _source.Dispose();
    }
}
