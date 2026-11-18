using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace DeskShell.Claude;

/// <summary>
/// A failed turn, with a message that is fine to show the user as-is
/// (for example "Claude isn't signed in…" or the last thing the CLI printed).
/// </summary>
public sealed class ClaudeChatException(string message) : Exception(message);

/// <summary>
/// One chat conversation with Claude, run through the installed <c>claude</c> CLI so it uses the
/// user's own Claude subscription (never an API key). Each turn starts the CLI once in print mode
/// and streams its JSON output back; later turns resume the same session so Claude remembers the chat.
/// The chat gets no tools at all, so it can't read or change anything on the machine.
/// </summary>
public sealed class ClaudeChat
{
    /// <summary>Shown when the CLI can't be found.</summary>
    public const string NotInstalledMessage = "Claude isn't installed — get Claude Code and sign in.";

    const string NotSignedInMessage = "Claude isn't signed in — run claude in a terminal and sign in.";

    readonly Dispatcher _dispatcher;
    readonly object _gate = new();
    Process? _process;
    bool _stopRequested;
    string? _sessionId;
    /// <summary>Bumped by <see cref="Reset"/>, so a turn still finishing from the old chat can't put its session back.</summary>
    int _generation;
    volatile bool _busy;

    /// <summary>Callbacks are delivered on <paramref name="dispatcher"/>, or on the thread that creates the chat (normally the UI thread).</summary>
    public ClaudeChat(Dispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        // A reply still streaming when DeskShell quits shouldn't leave a claude process behind.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
    }

    /// <summary>The CLI's session for this chat; null until the first reply has started.</summary>
    public string? SessionId
    {
        get { lock (_gate) return _sessionId; }
    }

    /// <summary>True while a turn is running (one at a time).</summary>
    public bool Busy => _busy;

    /// <summary>Where claude.exe is, or null. Looked up each time so installing Claude Code while DeskShell runs just works.</summary>
    public static string? ExePath => FindExe();

    /// <summary>Whether the Claude Code CLI is installed.</summary>
    public static bool Available => ExePath != null;

    static string? FindExe()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var native = Path.Combine(home, ".local", "bin", "claude.exe");
        if (File.Exists(native)) return native;

        // DeskShell starts at sign-in, so its own PATH can predate an install; the user's saved PATH catches that.
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "") + Path.PathSeparator
                  + (Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "");
        foreach (var raw in paths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(Environment.ExpandEnvironmentVariables(raw.Trim('"')), "claude.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { } // a malformed PATH entry
        }
        return null;
    }

    /// <summary>
    /// Sends one message and streams the reply. <paramref name="onDelta"/> gets each new piece of reply text;
    /// <paramref name="onStatus"/> gets short notes such as "Retrying…". Both run on the dispatcher, and all of
    /// them have run by the time the returned task completes.
    /// Throws <see cref="ClaudeChatException"/> (message safe to show) when the turn fails,
    /// <see cref="OperationCanceledException"/> after <see cref="Stop"/>, <see cref="Reset"/> or <paramref name="ct"/>,
    /// and <see cref="InvalidOperationException"/> if a turn is already running.
    /// </summary>
    public async Task SendAsync(string prompt, Action<string> onDelta, Action<string> onStatus, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;
        if (_busy) throw new InvalidOperationException("Claude is still answering.");
        var exe = ExePath ?? throw new ClaudeChatException(NotInstalledMessage);
        ct.ThrowIfCancellationRequested();

        _busy = true;
        lock (_gate) _stopRequested = false;
        try
        {
            var turn = await RunTurnAsync(exe, prompt, onDelta, onStatus, ct).ConfigureAwait(false);
            if (turn.MissingSession && !turn.GotText && !turn.Cancelled)
            {
                // The saved session is gone (cleaned up, or never written): carry on in a fresh one.
                lock (_gate) _sessionId = null;
                turn = await RunTurnAsync(exe, prompt, onDelta, onStatus, ct).ConfigureAwait(false);
            }
            if (turn.Cancelled) throw new OperationCanceledException(ct);
            if (turn.Error != null) throw new ClaudeChatException(Friendly(turn.Error));
        }
        finally
        {
            _busy = false;
            // Let every queued callback run before the caller carries on.
            try { await _dispatcher.InvokeAsync(() => { }); }
            catch (Exception e) when (e is TaskCanceledException or InvalidOperationException) { }
        }
    }

    /// <summary>Ends the reply that is streaming now (kills the CLI and anything it started).</summary>
    public void Stop()
    {
        Process? p;
        lock (_gate)
        {
            _stopRequested = true;
            p = _process;
        }
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException or ObjectDisposedException) { }
    }

    /// <summary>Starts a new conversation: stops any reply and forgets the session.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _generation++;
            _sessionId = null;
        }
        Stop();
    }

    sealed class Turn
    {
        public bool GotText;
        public bool SawResult;
        public bool Cancelled;
        public bool MissingSession;
        public string? Error;
    }

    async Task<Turn> RunTurnAsync(string exe, string prompt, Action<string> onDelta, Action<string> onStatus, CancellationToken ct)
    {
        var turn = new Turn();
        string? existing;
        int generation;
        lock (_gate)
        {
            existing = _sessionId;
            generation = _generation;
        }
        var id = existing ?? Guid.NewGuid().ToString();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = home
        };
        // The prompt goes right after -p: --tools takes a list and would swallow anything after it.
        // A leading space stops a message that starts with "-" being read as an option.
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(prompt.StartsWith('-') ? " " + prompt : prompt);
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--verbose");
        psi.ArgumentList.Add("--include-partial-messages");
        // No tools: chat can't touch files or run commands.
        psi.ArgumentList.Add("--tools");
        psi.ArgumentList.Add("");
        // The user's hooks report to DeskShell; chat runs shouldn't show up there.
        psi.ArgumentList.Add("--settings");
        psi.ArgumentList.Add("{\"disableAllHooks\":true}");
        psi.ArgumentList.Add(existing == null ? "--session-id" : "--resume");
        psi.ArgumentList.Add(id);
        // Use the subscription, never a key that happens to be in the environment; and don't look like
        // a nested Claude Code session if DeskShell itself was started from one.
        psi.Environment.Remove("ANTHROPIC_API_KEY");
        psi.Environment.Remove("CLAUDECODE");
        psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("The process didn't start.");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            turn.Error = "Couldn't start Claude: " + e.Message;
            return turn;
        }

        bool stopEarly;
        lock (_gate)
        {
            _process = proc;
            stopEarly = _stopRequested;
        }
        if (stopEarly) Stop();

        using var registration = ct.Register(Stop);
        try
        {
            // claude reads stdin in print mode; close it so it never waits for more input.
            try { proc.StandardInput.Close(); } catch (IOException) { }
            var stderrTask = proc.StandardError.ReadToEndAsync();

            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (line.Length == 0 || line[0] != '{') continue;
                HandleLine(line, turn, ref id, existing == null, generation, onDelta, onStatus);
            }

            await proc.WaitForExitAsync().ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            lock (_gate) turn.Cancelled = _stopRequested;
            if (turn.Cancelled || turn.SawResult) return turn;

            if (stderr.Contains("No conversation found", StringComparison.OrdinalIgnoreCase)) turn.MissingSession = true;
            if (proc.ExitCode != 0 || !turn.GotText)
                turn.Error = LastLines(stderr) ?? $"Claude stopped unexpectedly (exit code {proc.ExitCode}).";
            return turn;
        }
        finally
        {
            lock (_gate) if (_process == proc) _process = null;
            proc.Dispose();
        }
    }

    /// <summary>Reads one line of the CLI's stream-json output. Unknown kinds of line are ignored.</summary>
    void HandleLine(string line, Turn turn, ref string id, bool fresh, int generation, Action<string> onDelta, Action<string> onStatus)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            switch (Str(root, "type"))
            {
                case "stream_event":
                    if (root.TryGetProperty("event", out var ev) && ev.ValueKind == JsonValueKind.Object
                        && Str(ev, "type") == "content_block_delta"
                        && ev.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object
                        && Str(delta, "type") == "text_delta"
                        && Str(delta, "text") is { Length: > 0 } text)
                    {
                        turn.GotText = true;
                        // Text is flowing, so the session exists: keep it for the next message.
                        if (fresh) Commit(id, generation);
                        Post(onDelta, text);
                    }
                    break;

                case "system":
                    if (Str(root, "subtype") == "api_retry")
                    {
                        var attempt = root.TryGetProperty("attempt", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetInt32(out var n) ? n : 0;
                        Post(onStatus, attempt > 0 ? $"Retrying… (attempt {attempt})" : "Retrying…");
                    }
                    break;

                case "result":
                    turn.SawResult = true;
                    if (Str(root, "session_id") is { Length: > 0 } sid) id = sid;
                    var isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
                    var result = Str(root, "result");
                    if (isError)
                    {
                        turn.Error = string.IsNullOrWhiteSpace(result)
                            ? (Str(root, "subtype") is { Length: > 0 } sub ? $"Claude couldn't answer ({sub})." : "Claude couldn't answer.")
                            : result.Trim();
                        if (turn.Error.Contains("No conversation found", StringComparison.OrdinalIgnoreCase)) turn.MissingSession = true;
                        else if (turn.GotText) Commit(id, generation);
                    }
                    else
                    {
                        Commit(id, generation);
                        // Some runs don't stream partial messages; the final text is still in the result.
                        if (!turn.GotText && !string.IsNullOrEmpty(result))
                        {
                            turn.GotText = true;
                            Post(onDelta, result);
                        }
                    }
                    break;
            }
        }
    }

    void Commit(string id, int generation)
    {
        lock (_gate)
            if (_generation == generation) _sessionId = id;
    }

    void Post(Action<string> callback, string value) => _dispatcher.InvokeAsync(() => callback(value));

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>The last few lines the CLI printed to stderr, short enough to show in a line of UI.</summary>
    static string? LastLines(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) return null;
        var text = string.Join(" ", lines.TakeLast(3));
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    /// <summary>Turns the CLI's sign-in errors into something a person can act on.</summary>
    static string Friendly(string error)
    {
        if (error.Contains("/login", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Invalid API key", StringComparison.OrdinalIgnoreCase)
            || error.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
            || error.Contains("OAuth token", StringComparison.OrdinalIgnoreCase))
            return NotSignedInMessage;
        return error;
    }
}
