using System.Collections.ObjectModel;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;

namespace DeskShell.Claude;

public enum ClaudeState { Idle, Working, NeedsYou, Done }

/// <summary>One Claude Code session (a terminal, the desktop app's Code tab, an IDE), as its hooks report it.</summary>
public sealed class ClaudeSession
{
    public required string Id { get; init; }
    public string Cwd { get; set; } = "";
    public string? Title { get; set; }
    public string Project => Title ?? (Cwd.Length > 0 ? Path.GetFileName(Cwd.TrimEnd('\\', '/')) : "Claude");
    public ClaudeState State { get; set; }
    /// <summary>What it's doing or last did, in words ("Editing App.xaml.cs").</summary>
    public string Action { get; set; } = "";
    public string Prompt { get; set; } = "";
    public DateTime Updated { get; set; } = DateTime.Now;
    /// <summary>When the current piece of work started (the prompt), for "worked for 3 min".</summary>
    public DateTime WorkStarted { get; set; } = DateTime.Now;
}

/// <summary>A permission request waiting for an answer in the notch.</summary>
public sealed class ClaudeApproval
{
    public required string SessionId { get; init; }
    public required string Project { get; init; }
    public required string Tool { get; init; }
    /// <summary>What exactly it wants to do: the command, the file…</summary>
    public required string Summary { get; init; }
    public JsonNode? Suggestions { get; init; }
    public DateTime Arrived { get; } = DateTime.Now;
    internal TaskCompletionSource<string?> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public enum ClaudeAnswer { Allow, AlwaysAllow, Deny, AnswerInClaude }

/// <summary>
/// Listens for the hook events Claude Code sends (through <see cref="ClaudeHook"/>), keeps a live list
/// of sessions and what they're doing, and holds permission requests until you answer them in the
/// notch. Everything here runs on the UI thread except the pipe reading.
/// </summary>
public static class ClaudeHub
{
    static Dispatcher? _ui;
    static CancellationTokenSource? _stop;
    static DispatcherTimer? _tidy;

    public static ObservableCollection<ClaudeSession> Sessions { get; } = new();
    public static ObservableCollection<ClaudeApproval> Approvals { get; } = new();

    /// <summary>Anything changed (UI thread).</summary>
    public static event Action? Changed;
    /// <summary>A session finished a piece of work (UI thread).</summary>
    public static event Action<ClaudeSession, TimeSpan>? Finished;
    /// <summary>A permission request arrived (UI thread).</summary>
    public static event Action<ClaudeApproval>? ApprovalArrived;

    public static bool Running => _stop != null;

    public static void Start(Dispatcher ui)
    {
        if (_stop != null) return;
        _ui = ui;
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        new Thread(() => Listen(token)) { IsBackground = true, Name = "DeskShell Claude hooks" }.Start();
        // Forget sessions that went quiet hours ago (closed without saying so).
        _tidy = new DispatcherTimer(TimeSpan.FromMinutes(5), DispatcherPriority.Background, (_, _) => Tidy(), ui);
        _tidy.Start();
    }

    public static void Stop()
    {
        _stop?.Cancel();
        _stop = null;
        _tidy?.Stop();
        foreach (var a in Approvals.ToList()) a.Answer.TrySetResult(null);
    }

    /// <summary>The pet's mood: something to answer beats working, working beats idle.</summary>
    public static ClaudeState Overall =>
        Approvals.Count > 0 ? ClaudeState.NeedsYou
        : Sessions.Any(s => s.State == ClaudeState.Working && DateTime.Now - s.Updated < TimeSpan.FromMinutes(10)) ? ClaudeState.Working
        : ClaudeState.Idle;

    public static void Answer(ClaudeApproval approval, ClaudeAnswer answer)
    {
        string? reply = answer switch
        {
            ClaudeAnswer.Allow => Decision(new JsonObject { ["behavior"] = "allow" }),
            ClaudeAnswer.AlwaysAllow => Decision(new JsonObject
            {
                ["behavior"] = "allow",
                // Claude Code suggests the right "always" rule itself; without one, allow this tool for the session.
                ["updatedPermissions"] = approval.Suggestions?.DeepClone() ?? new JsonArray(new JsonObject
                {
                    ["type"] = "addRules",
                    ["rules"] = new JsonArray(new JsonObject { ["toolName"] = approval.Tool }),
                    ["behavior"] = "allow",
                    ["destination"] = "session"
                })
            }),
            ClaudeAnswer.Deny => Decision(new JsonObject { ["behavior"] = "deny", ["message"] = "Denied from DeskShell's notch" }),
            _ => null // Claude shows its own prompt
        };
        approval.Answer.TrySetResult(reply);
        Approvals.Remove(approval);
        if (Sessions.FirstOrDefault(s => s.Id == approval.SessionId) is { } session && session.State == ClaudeState.NeedsYou)
        {
            session.State = answer == ClaudeAnswer.Deny ? ClaudeState.Idle : ClaudeState.Working;
            session.Action = answer switch
            {
                ClaudeAnswer.Deny => "You said no: " + approval.Summary,
                ClaudeAnswer.AnswerInClaude => "Asking you in Claude",
                _ => approval.Summary
            };
            session.Updated = DateTime.Now;
        }
        Changed?.Invoke();
    }

    static string Decision(JsonObject decision) =>
        new JsonObject { ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PermissionRequest", ["decision"] = decision } }.ToJsonString();

    // ---------- the pipe ----------

    static void Listen(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(ClaudeHook.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                pipe.WaitForConnectionAsync(token).GetAwaiter().GetResult();
                var connection = pipe;
                pipe = null;
                // Each hook gets its own task, so a waiting approval doesn't hold up the others.
                _ = Task.Run(() => Serve(connection));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error("Claude hook pipe failed", ex);
                Thread.Sleep(500);
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    static async Task Serve(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 8192, leaveOpen: true);
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) return;
                var message = JsonNode.Parse(line) as JsonObject;
                var name = message?["event"]?.GetValue<string>() ?? "";
                if (message?["payload"] is not JsonObject payload) return;

                if (name != "PermissionRequest")
                {
                    _ui?.BeginInvoke(() => Handle(name, payload));
                    return;
                }

                // Hold the request until it's answered (or the hook gives up and closes the pipe).
                var approval = await _ui!.InvokeAsync(() => Ask(payload));
                var closed = WaitForClose(pipe);
                var done = await Task.WhenAny(approval.Answer.Task, closed, Task.Delay(TimeSpan.FromSeconds(ClaudeHook.ApprovalWaitSeconds + 5)));
                if (done != approval.Answer.Task)
                {
                    // Not answered here in time: Claude's own prompt takes over.
                    _ui.BeginInvoke(() => Drop(approval));
                    return;
                }
                var reply = approval.Answer.Task.Result ?? "";
                var bytes = Encoding.UTF8.GetBytes(reply + "\n");
                await pipe.WriteAsync(bytes);
                await pipe.FlushAsync();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // the hook went away
            }
            catch (Exception ex)
            {
                Log.Error("Couldn't handle a Claude hook", ex);
            }
        }
    }

    /// <summary>Completes when the other end hangs up (the hook gave up waiting).</summary>
    static async Task WaitForClose(PipeStream pipe)
    {
        while (pipe.IsConnected) await Task.Delay(500);
    }

    // ---------- events → sessions ----------

    static ClaudeSession SessionFor(JsonObject p)
    {
        var id = p["session_id"]?.GetValue<string>() ?? "";
        var session = Sessions.FirstOrDefault(s => s.Id == id);
        if (session == null)
        {
            session = new ClaudeSession { Id = id };
            Sessions.Insert(0, session);
        }
        if (p["cwd"]?.GetValue<string>() is { Length: > 0 } cwd) session.Cwd = cwd;
        if (p["session_title"]?.GetValue<string>() is { Length: > 0 } title) session.Title = title;
        session.Updated = DateTime.Now;
        return session;
    }

    static void Handle(string name, JsonObject p)
    {
        if (p["agent_id"] != null && name is "Stop" or "SessionStart") return; // a subagent's, not the session's
        var session = SessionFor(p);
        switch (name)
        {
            case "SessionStart":
                session.State = ClaudeState.Idle;
                session.Action = "Ready";
                break;
            case "UserPromptSubmit":
                session.State = ClaudeState.Working;
                session.WorkStarted = DateTime.Now;
                session.Prompt = Shorten(p["prompt"]?.GetValue<string>() ?? "", 140);
                session.Action = "Thinking…";
                break;
            case "PreToolUse":
                if (session.State != ClaudeState.NeedsYou) session.State = ClaudeState.Working;
                session.Action = Describe(p["tool_name"]?.GetValue<string>() ?? "", p["tool_input"]);
                break;
            case "PostToolUse":
                if (session.State == ClaudeState.Idle) session.State = ClaudeState.Working;
                break;
            case "Notification":
                if (p["notification_type"]?.GetValue<string>() == "idle_prompt" && session.State != ClaudeState.Working)
                    session.Action = "Waiting for you";
                break;
            case "Stop":
                var took = DateTime.Now - session.WorkStarted;
                session.State = ClaudeState.Done;
                session.Action = "Finished";
                Finished?.Invoke(session, took);
                break;
            case "SessionEnd":
                Sessions.Remove(session);
                foreach (var a in Approvals.Where(a => a.SessionId == session.Id).ToList()) Drop(a);
                break;
        }
        Changed?.Invoke();
    }

    static ClaudeApproval Ask(JsonObject p)
    {
        var session = SessionFor(p);
        var tool = p["tool_name"]?.GetValue<string>() ?? "a tool";
        var approval = new ClaudeApproval
        {
            SessionId = session.Id,
            Project = session.Project,
            Tool = tool,
            Summary = Describe(tool, p["tool_input"]),
            Suggestions = p["permission_suggestions"]?.DeepClone()
        };
        session.State = ClaudeState.NeedsYou;
        session.Action = "Needs your OK: " + approval.Summary;
        Approvals.Add(approval);
        ApprovalArrived?.Invoke(approval);
        Changed?.Invoke();
        return approval;
    }

    static void Drop(ClaudeApproval approval)
    {
        approval.Answer.TrySetResult(null);
        if (!Approvals.Remove(approval)) return;
        if (Sessions.FirstOrDefault(s => s.Id == approval.SessionId) is { State: ClaudeState.NeedsYou } session)
            session.Action = "Asking you in Claude";
        Changed?.Invoke();
    }

    static void Tidy()
    {
        var changed = false;
        foreach (var s in Sessions.Where(s => DateTime.Now - s.Updated > TimeSpan.FromHours(3)).ToList())
        {
            Sessions.Remove(s);
            changed = true;
        }
        foreach (var s in Sessions.Where(s => s.State == ClaudeState.Done && DateTime.Now - s.Updated > TimeSpan.FromMinutes(10)))
        {
            s.State = ClaudeState.Idle;
            changed = true;
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>A tool call in words: "Running dotnet build", "Editing App.xaml.cs", "Reading the web".</summary>
    public static string Describe(string tool, JsonNode? input)
    {
        string? S(string key) => input?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        string File(string key) => Path.GetFileName(S(key) ?? "") is { Length: > 0 } f ? f : "a file";
        return tool switch
        {
            "Bash" or "PowerShell" => "Running " + Shorten(S("command") ?? "a command", 80),
            "Edit" or "MultiEdit" => "Editing " + File("file_path"),
            "Write" => "Writing " + File("file_path"),
            "Read" => "Reading " + File("file_path"),
            "NotebookEdit" => "Editing " + File("notebook_path"),
            "Grep" => "Searching for " + Shorten(S("pattern") ?? "text", 40),
            "Glob" => "Finding " + Shorten(S("pattern") ?? "files", 40),
            "WebFetch" => "Reading " + Shorten(S("url") ?? "a web page", 60),
            "WebSearch" => "Searching the web for " + Shorten(S("query") ?? "", 50),
            "Task" or "Agent" => "Starting a helper: " + Shorten(S("description") ?? "", 50),
            _ when tool.StartsWith("mcp__") => "Using " + tool.Split("__").Last().Replace('_', ' '),
            _ => "Using " + tool
        };
    }

    static string Shorten(string text, int max)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}
