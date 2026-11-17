using System.Media;
using System.Windows.Threading;

namespace DeskShell.Services;

/// <summary>A focus timer: work for a while (25 minutes by default), then a short break. Shared by every notch.</summary>
public static class FocusTimer
{
    public enum Phase { Ready, Focus, Break }

    static DispatcherTimer? _timer;
    static DateTime _endsAt;

    public static Phase Current { get; private set; } = Phase.Ready;
    public static bool Running { get; private set; }
    public static TimeSpan Remaining { get; private set; } = TimeSpan.FromMinutes(App.Settings.Notch.FocusMinutes);
    public static int DoneToday { get; private set; }
    static DateTime _doneDay = DateTime.Today;

    /// <summary>Raised on the UI thread every second while running, and on every change.</summary>
    public static event Action? Changed;

    public static string Status => Current switch
    {
        Phase.Focus => Running ? "Focusing" : "Paused",
        Phase.Break => Running ? "On a break" : "Break paused",
        _ => DoneToday > 0 ? $"Ready · {DoneToday} done today" : "Ready"
    };

    public static string Text => $"{(int)Remaining.TotalMinutes:00}:{Remaining.Seconds:00}";

    public static void Toggle()
    {
        if (Running) Pause();
        else Start();
    }

    public static void Start()
    {
        if (Current == Phase.Ready) Current = Phase.Focus;
        _endsAt = DateTime.Now + Remaining;
        Running = true;
        _timer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher.CurrentDispatcher);
        _timer.Start();
        Changed?.Invoke();
    }

    public static void Pause()
    {
        if (!Running) return;
        Remaining = Max0(_endsAt - DateTime.Now);
        Running = false;
        _timer?.Stop();
        Changed?.Invoke();
    }

    public static void Reset()
    {
        Running = false;
        _timer?.Stop();
        Current = Phase.Ready;
        Remaining = TimeSpan.FromMinutes(App.Settings.Notch.FocusMinutes);
        Changed?.Invoke();
    }

    /// <summary>Ends this part now: focus goes to the break, a break back to ready.</summary>
    public static void Skip() => Finish(sound: false);

    public static void SetMinutes(int minutes)
    {
        var updated = App.Settings.Notch;
        updated.FocusMinutes = Math.Clamp(minutes, 1, 180);
        App.Settings.Save();
        if (Current == Phase.Ready && !Running) Remaining = TimeSpan.FromMinutes(updated.FocusMinutes);
        Changed?.Invoke();
    }

    static void Tick()
    {
        Remaining = Max0(_endsAt - DateTime.Now);
        if (Remaining <= TimeSpan.Zero) Finish(sound: true);
        else Changed?.Invoke();
    }

    static void Finish(bool sound)
    {
        if (sound) SystemSounds.Asterisk.Play();
        if (Current == Phase.Focus)
        {
            if (_doneDay != DateTime.Today)
            {
                _doneDay = DateTime.Today;
                DoneToday = 0;
            }
            DoneToday++;
            Current = Phase.Break;
            Remaining = TimeSpan.FromMinutes(App.Settings.Notch.BreakMinutes);
            Start(); // the break runs on its own
            return;
        }
        Reset();
    }

    static TimeSpan Max0(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Ceiling(t.TotalSeconds));
}
