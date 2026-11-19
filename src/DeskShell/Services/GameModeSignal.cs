namespace DeskShell.Services;

/// <summary>
/// Game mode, shared with DeskShell's other processes (the file explorer and its file index) through
/// a named event: the shell sets it while a game runs, the others check it before doing background work.
/// </summary>
public static class GameModeSignal
{
    const string Name = "DeskShell.GameModeActive";
    static EventWaitHandle? _event;
    static bool _owner;
    static long _lastTry;

    /// <summary>Called by the shell when game mode starts or ends.</summary>
    public static void Set(bool on)
    {
        try
        {
            _event ??= new EventWaitHandle(false, EventResetMode.ManualReset, Name);
            _owner = true;
            if (on) _event.Set();
            else _event.Reset();
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't share game mode with DeskShell's other processes", ex);
        }
    }

    /// <summary>True while a game runs (as far as the shell says).</summary>
    public static bool Active
    {
        get
        {
            if (_event == null && !_owner)
            {
                // The shell may start after us; look again now and then.
                var now = Environment.TickCount64;
                if (now - _lastTry < 10_000) return false;
                _lastTry = now;
                if (!EventWaitHandle.TryOpenExisting(Name, out var opened)) return false;
                _event = opened;
            }
            try
            {
                return _event?.WaitOne(0) == true;
            }
            catch
            {
                return false;
            }
        }
    }
}
