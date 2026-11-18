using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;

namespace DeskShell.Sage;

/// <summary>How risky Sage thinks a tool call is (its RiskLevel numbers).</summary>
public enum SageRisk { ReadOnly, Reversible, Destructive, Elevated }

/// <summary>Something Sage wants your OK for before it runs a tool.</summary>
public sealed class SageApproval
{
    public required string Id { get; init; }
    public required string Tool { get; init; }
    public required string Summary { get; init; }
    public string Target { get; init; } = "";
    public SageRisk Risk { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public string? GrantLabel { get; init; }
    public int GrantMinutes { get; init; }
}

public sealed class SageException(string message) : Exception(message);

/// <summary>
/// DeskShell's line to Sage (the user's PCAssistant): the same private pipe Sage's own window uses
/// (current user only, plus Sage's access token from its data folder). Sage keeps running as its own
/// processes; this only listens to what it's doing and passes messages, answers and switches to it.
/// When Sage isn't running it quietly tries again every few seconds.
/// </summary>
public static class SageLink
{
    const string PipeName = "PCAssistant.Host.v1";
    const string ShowEventName = @"Local\PCAssistant.Desktop.Show";
    static readonly string LocalData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static readonly string InstallDir = Path.Combine(LocalData, "Programs", "PCAssistant");
    static readonly string TokenFile = Path.Combine(LocalData, "PCAssistant", "data", ".ipc-token");
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    static Dispatcher? _ui;
    static CancellationTokenSource? _stop;
    static NamedPipeClientStream? _pipe;
    static StreamWriter? _writer;
    static string _token = "";
    static readonly SemaphoreSlim WriteLock = new(1, 1);
    static readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> Pending = new();

    public static bool Installed => File.Exists(Path.Combine(InstallDir, "PCAssistant.Bootstrap.exe"));
    public static bool Connected { get; private set; }
    /// <summary>Sage's AssistantState name: Offline, Idle, Listening, Thinking, Acting, Speaking, Error, Observing, Researching.</summary>
    public static string State { get; private set; } = "Offline";
    /// <summary>For Acting / Researching: the tool it's using.</summary>
    public static string? Detail { get; private set; }
    public static string Name { get; private set; } = "Sage";
    public static bool WakeEnabled { get; private set; }
    public static bool SpeakEnabled { get; private set; }
    public static bool StartHidden { get; private set; }
    /// <summary>Whether Sage is talking right now (its audio playback).</summary>
    public static bool Speaking { get; private set; }
    public static ObservableCollection<SageApproval> Approvals { get; } = [];

    // All events are raised on the UI thread.
    public static event Action? Changed;
    public static event Action<string, string>? ChatDelta;          // operationId, text
    public static event Action<string, string>? ChatCompleted;      // operationId, full reply
    public static event Action<string, string, bool>? ChatFailed;   // operationId, error, cancelled
    public static event Action<string, string, string>? VoiceChatStarted; // operationId, conversationId, what you said
    public static event Action? WakeDetected;
    public static event Action<string>? Heard;                      // a transcript of what you said
    public static event Action<double>? Level;                      // playback level 0..1 while Sage speaks
    public static event Action<SageApproval>? ApprovalArrived;
    public static event Action<SageApproval>? ApprovalGone;

    public static void Start(Dispatcher ui)
    {
        if (_stop != null) return;
        _ui = ui;
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _ = Task.Run(() => RunAsync(token));
    }

    public static void Stop()
    {
        _stop?.Cancel();
        try { _pipe?.Dispose(); } catch { }
    }

    static async Task RunAsync(CancellationToken stop)
    {
        var loggedFailure = false;
        while (!stop.IsCancellationRequested)
        {
            if (File.Exists(TokenFile))
            {
                try
                {
                    _token = File.ReadAllText(TokenFile).Trim();
                    var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.ConnectAsync(300, stop);
                    _pipe = pipe;
                    _writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                    using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                    Log.Info("Connected to Sage");
                    loggedFailure = false;
                    var read = ReadAsync(reader, stop);
                    await OnConnectedAsync();
                    await read;
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (TimeoutException) { }
                catch (Exception ex)
                {
                    if (!loggedFailure) Log.Info($"Sage link: {ex.Message}");
                    loggedFailure = true;
                }
                finally { Disconnected(); }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stop); } catch { break; }
        }
    }

    static async Task ReadAsync(StreamReader reader, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(stop);
            if (line == null) break;
            if (line.Length == 0) continue;
            try { Dispatch(line); }
            catch (Exception ex) { Log.Error("Couldn't read a message from Sage", ex); }
        }
    }

    static void Disconnected()
    {
        var was = Connected;
        try { _writer?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        _writer = null;
        _pipe = null;
        foreach (var (id, waiting) in Pending)
            if (Pending.TryRemove(id, out _)) waiting.TrySetException(new SageException("Sage closed."));
        if (!was) return;
        Log.Info("Sage link closed");
        Ui(() =>
        {
            Connected = false;
            State = "Offline";
            Detail = null;
            Speaking = false;
            foreach (var approval in Approvals.ToList()) { Approvals.Remove(approval); ApprovalGone?.Invoke(approval); }
            Changed?.Invoke();
        });
    }

    static async Task OnConnectedAsync()
    {
        string state = "Idle";
        string? detail = null;
        try
        {
            // Newer Sage answers this; an older one says it doesn't know the method, and the next event fills it in.
            var now = await SendAsync("state.get", null, null, TimeSpan.FromSeconds(3), false);
            state = StateName(now.GetProperty("state"));
            detail = Str(now, "detail");
        }
        catch { }
        Dictionary<string, string>? settings = null;
        try
        {
            var all = await SendAsync("settings.get", null, null, TimeSpan.FromSeconds(5), false);
            settings = all.Deserialize<Dictionary<string, string>>(Json);
        }
        catch (Exception ex) { Log.Info($"Couldn't read Sage's settings: {ex.Message}"); }

        Ui(() =>
        {
            Connected = true;
            State = state;
            Detail = detail;
            if (settings != null) ApplySettings(settings);
            Changed?.Invoke();
        });
    }

    static void ApplySettings(Dictionary<string, string> settings)
    {
        bool On(string key, bool fallback) => settings.TryGetValue(key, out var v) ? string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) : fallback;
        WakeEnabled = On("voice.wakeEnabled", false);
        SpeakEnabled = On("voice.ttsEnabled", false);
        StartHidden = On("ui.startHidden", false);
        if (settings.TryGetValue("assistant.name", out var name) && !string.IsNullOrWhiteSpace(name)) Name = name.Trim();
    }

    // ---------- calls ----------

    /// <summary>Calls one of Sage's methods and returns its result. Throws <see cref="SageException"/> when Sage isn't there or says no.</summary>
    public static Task<JsonElement> CallAsync(string method, object? parameters = null, string? operationId = null, TimeSpan? timeout = null) =>
        SendAsync(method, parameters, operationId, timeout, requireConnected: true);

    static async Task<JsonElement> SendAsync(string method, object? parameters, string? operationId, TimeSpan? timeout, bool requireConnected)
    {
        var writer = _writer;
        if (writer == null || (requireConnected && !Connected)) throw new SageException($"{Name} isn't running.");
        var wait = timeout ?? TimeSpan.FromSeconds(15);
        var id = Guid.NewGuid().ToString("N");
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = JsonSerializer.SerializeToNode(parameters ?? new { }, Json),
            ["protocolVersion"] = 1,
            ["operationId"] = operationId ?? Guid.NewGuid().ToString("N"),
            ["deadlineUnixMs"] = DateTimeOffset.UtcNow.Add(wait).ToUnixTimeMilliseconds(),
            ["authToken"] = _token
        };
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending[id] = answer;
        try
        {
            await WriteLock.WaitAsync();
            try { await writer.WriteLineAsync(request.ToJsonString()); }
            finally { WriteLock.Release(); }
            return await answer.Task.WaitAsync(wait);
        }
        catch (TimeoutException) { throw new SageException($"{Name} didn't answer."); }
        catch (IOException) { throw new SageException($"{Name} isn't running."); }
        catch (ObjectDisposedException) { throw new SageException($"{Name} isn't running."); }
        finally { Pending.TryRemove(id, out _); }
    }

    static void Dispatch(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
        {
            if (!Pending.TryGetValue(idProp.GetString()!, out var waiting)) return;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                waiting.TrySetException(new SageException(Str(error, "message") ?? "Sage said no."));
            else
                waiting.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
            return;
        }
        if (!root.TryGetProperty("method", out var methodProp)) return;
        var p = root.TryGetProperty("params", out var ps) ? ps.Clone() : default;
        var method = methodProp.GetString();
        Ui(() => OnEvent(method, p));
    }

    static void OnEvent(string? method, JsonElement p)
    {
        switch (method)
        {
            case "event.state.changed":
                State = p.TryGetProperty("state", out var s) ? StateName(s) : State;
                Detail = Str(p, "detail");
                Changed?.Invoke();
                break;
            case "event.chat.delta":
                ChatDelta?.Invoke(Str(p, "operationId") ?? "", Str(p, "text") ?? "");
                break;
            case "event.chat.completed":
                var content = p.TryGetProperty("message", out var message) ? Str(message, "content") ?? "" : "";
                ChatCompleted?.Invoke(Str(p, "operationId") ?? "", content);
                break;
            case "event.chat.failed":
                ChatFailed?.Invoke(Str(p, "operationId") ?? "", Str(p, "error") ?? "Something went wrong.",
                    p.TryGetProperty("cancelled", out var c) && c.ValueKind == JsonValueKind.True);
                break;
            case "event.audio":
                OnAudio(Str(p, "eventType"), p.TryGetProperty("data", out var data) ? data : default);
                break;
            case "event.approval.required":
                OnApproval(p);
                break;
            case "event.approval.resolved":
                Resolved(Str(p, "approvalId"));
                break;
        }
    }

    static void OnAudio(string? type, JsonElement data)
    {
        switch (type)
        {
            case "audio.wake_detected":
                WakeDetected?.Invoke();
                break;
            case "audio.transcript":
                var text = Str(data, "text");
                if (!string.IsNullOrWhiteSpace(text)) Heard?.Invoke(text.Trim());
                break;
            case "audio.voice_chat_started":
                VoiceChatStarted?.Invoke(Str(data, "operationId") ?? "", Str(data, "conversationId") ?? "", Str(data, "text") ?? "");
                break;
            case "audio.playback_state":
                var speaking = Str(data, "state") == "playing";
                if (speaking == Speaking) break;
                Speaking = speaking;
                Changed?.Invoke();
                break;
            case "audio.playback_level":
                if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("level", out var level) && level.TryGetDouble(out var l))
                    Level?.Invoke(Math.Clamp(l, 0, 1));
                break;
        }
    }

    static void OnApproval(JsonElement p)
    {
        var id = Str(p, "id");
        if (id == null || Approvals.Any(a => a.Id == id)) return;
        var tool = p.TryGetProperty("invocation", out var inv) ? Str(inv, "name") ?? "a tool" : "a tool";
        var approval = new SageApproval
        {
            Id = id,
            Tool = tool,
            Summary = Str(p, "summary") ?? tool,
            Target = Str(p, "exactTarget") ?? "",
            Risk = p.TryGetProperty("risk", out var r) && r.TryGetInt32(out var risk) ? (SageRisk)risk : SageRisk.Reversible,
            ExpiresAt = p.TryGetProperty("expiresAt", out var e) && e.TryGetDateTimeOffset(out var at) ? at : DateTimeOffset.Now.AddMinutes(1),
            GrantLabel = Str(p, "grantLabel"),
            GrantMinutes = p.TryGetProperty("grantMinutes", out var g) && g.TryGetInt32(out var minutes) ? minutes : 0
        };
        Approvals.Add(approval);
        // Sage gives up on its own after the expiry time; the card goes then too.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(1, (approval.ExpiresAt - DateTimeOffset.Now).TotalSeconds + 1)) };
        timer.Tick += (_, _) => { timer.Stop(); Resolved(approval.Id); };
        timer.Start();
        ApprovalArrived?.Invoke(approval);
        Changed?.Invoke();
    }

    static void Resolved(string? id)
    {
        var approval = Approvals.FirstOrDefault(a => a.Id == id);
        if (approval == null) return;
        Approvals.Remove(approval);
        ApprovalGone?.Invoke(approval);
        Changed?.Invoke();
    }

    /// <summary>Answers a request. <paramref name="grant"/> also allows the same thing for the minutes Sage offers.</summary>
    public static async Task AnswerAsync(SageApproval approval, bool approved, bool grant = false)
    {
        Resolved(approval.Id);
        try
        {
            await CallAsync("approval.respond", new { approvalId = approval.Id, approved, grantMinutes = grant ? approval.GrantMinutes : 0 });
        }
        catch (Exception ex) { Log.Info($"Couldn't answer Sage: {ex.Message}"); }
    }

    // ---------- switches ----------

    public static async Task SetWakeAsync(bool on)
    {
        WakeEnabled = on;
        Changed?.Invoke();
        await CallAsync("settings.set", new { key = "voice.wakeEnabled", value = on ? "true" : "false" });
        await CallAsync("audio.command", new { method = on ? "wake.start" : "wake.stop", @params = new { } });
    }

    public static async Task SetSpeakAsync(bool on)
    {
        SpeakEnabled = on;
        Changed?.Invoke();
        await CallAsync("settings.set", new { key = "voice.ttsEnabled", value = on ? "true" : "false" });
    }

    public static async Task SetStartHiddenAsync(bool on)
    {
        StartHidden = on;
        Changed?.Invoke();
        await CallAsync("settings.set", new { key = "ui.startHidden", value = on ? "true" : "false" });
    }

    /// <summary>Starts listening right away, like Sage's own microphone button.</summary>
    public static Task ListenAsync() => CallAsync("audio.command", new { method = "capture.start", @params = new { } });

    public static Task HushAsync() => CallAsync("tts.stop");

    // ---------- Sage's own window ----------

    /// <summary>Brings up Sage's own window, starting Sage first if it isn't running.</summary>
    public static bool OpenWindow()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var show))
            {
                // Windows only lets an app take the front if the one in front says so.
                foreach (var desktop in System.Diagnostics.Process.GetProcessesByName("PCAssistant.Desktop"))
                    using (desktop) AllowSetForegroundWindow(desktop.Id);
                using (show) show.Set();
                return true;
            }
            if (!Running()) return StartSage();
            Log.Info("Sage's window can't be opened from here (its Desktop doesn't listen for DeskShell yet)");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't open Sage", ex);
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);

    public static bool Running() => System.Diagnostics.Process.GetProcessesByName("PCAssistant.Host").Length > 0;

    public static bool StartSage()
    {
        var exe = Path.Combine(InstallDir, "PCAssistant.Bootstrap.exe");
        if (!File.Exists(exe)) return false;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = InstallDir })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't start Sage", ex);
            return false;
        }
    }

    // ---------- helpers ----------

    static string StateName(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "Idle",
        JsonValueKind.Number when value.TryGetInt32(out var n) => n switch
        {
            0 => "Offline", 1 => "Idle", 2 => "Listening", 3 => "Thinking", 4 => "Acting",
            5 => "Speaking", 6 => "Error", 7 => "Observing", 8 => "Researching", _ => "Idle"
        },
        _ => "Idle"
    };

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static void Ui(Action action)
    {
        if (_ui == null || _ui.CheckAccess()) action();
        else _ui.BeginInvoke(action);
    }
}
