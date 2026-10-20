using Windows.UI.Shell;

namespace DeskShell.Services;

/// <summary>
/// Windows 11 Focus, which turns on Do Not Disturb while it runs. Windows has no public way to
/// flip Do Not Disturb on its own, so this is the closest supported switch.
/// </summary>
internal static class FocusService
{
    static FocusSessionManager? _manager;
    static bool _checked;

    public static event Action? Changed;

    static FocusSessionManager? Manager
    {
        get
        {
            if (_checked) return _manager;
            _checked = true;
            try
            {
                if (FocusSessionManager.IsSupported)
                {
                    _manager = FocusSessionManager.GetDefault();
                    _manager.IsFocusActiveChanged += (_, _) => Changed?.Invoke();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Focus sessions are not available", ex);
            }
            Log.Info($"Focus sessions supported: {_manager != null}");
            return _manager;
        }
    }

    public static bool IsSupported => Manager != null;

    public static bool IsActive
    {
        get
        {
            try { return Manager?.IsFocusActive == true; }
            catch { return false; }
        }
    }

    public static void SetActive(bool on)
    {
        try
        {
            if (on) Manager?.TryStartFocusSession();
            else Manager?.DeactivateFocus();
        }
        catch (Exception ex)
        {
            Log.Error("Could not switch Focus", ex);
        }
    }
}
