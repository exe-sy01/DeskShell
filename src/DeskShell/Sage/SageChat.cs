using System.Text.Json;
using DeskShell.Notch;

namespace DeskShell.Sage;

/// <summary>
/// The Sage tab's chat: messages go into one of Sage's own conversations (the most recent one of yours,
/// like Sage's command line does), so they're in its window's history too. Replies stream back from Sage.
/// </summary>
sealed class SageChat : IChatBackend
{
    const string DiscordContactPrefix = "Discord · ";
    const string DiscordBridge = "Discord bridge";

    string? _operation;
    bool _fresh;

    /// <summary>The Sage conversation this chat is in; null until the first message (or after New chat).</summary>
    public string? ConversationId { get; set; }

    public string Name => SageLink.Name;
    public bool Available => SageLink.Connected;
    public string UnavailableMessage => SageLink.Installed
        ? $"{SageLink.Name} isn't running. Start it with the button on the left."
        : "Sage (PCAssistant) isn't installed.";
    public string Welcome => $"Talk to {SageLink.Name}. It's the same {SageLink.Name} as in its own window, with its tools, and it asks before anything risky.";
    public bool Busy => _operation != null;

    public async Task SendAsync(string prompt, Action<string> onDelta, Action<string> onStatus, CancellationToken ct)
    {
        if (_operation != null) throw new InvalidOperationException($"{Name} is still answering.");
        var op = _operation = Guid.NewGuid().ToString("N");
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gotText = false;

        void Delta(string id, string text)
        {
            if (id != op || text.Length == 0) return;
            gotText = true;
            onDelta(text);
        }
        void Completed(string id, string content)
        {
            if (id != op) return;
            // A provider that doesn't stream sends it all at the end.
            if (!gotText && content.Length > 0) onDelta(content);
            done.TrySetResult();
        }
        void Failed(string id, string error, bool cancelled)
        {
            if (id != op) return;
            if (cancelled) done.TrySetCanceled();
            else done.TrySetException(new SageException(error));
        }
        void StateChanged()
        {
            if (!SageLink.Connected) { done.TrySetException(new SageException($"{Name} closed.")); return; }
            var note = Describe(SageLink.State, SageLink.Detail);
            if (note != null) onStatus(note);
        }

        SageLink.ChatDelta += Delta;
        SageLink.ChatCompleted += Completed;
        SageLink.ChatFailed += Failed;
        SageLink.Changed += StateChanged;
        try
        {
            var conversation = await ConversationAsync();
            ct.ThrowIfCancellationRequested();
            await SageLink.CallAsync("chat.start", new { conversationId = conversation, text = prompt }, op);
            using (ct.Register(() =>
            {
                done.TrySetCanceled();
                _ = SageLink.CallAsync("chat.cancel", new { operationId = op }).ContinueWith(_ => { }, TaskScheduler.Default);
            }))
            {
                await done.Task;
            }
        }
        finally
        {
            SageLink.ChatDelta -= Delta;
            SageLink.ChatCompleted -= Completed;
            SageLink.ChatFailed -= Failed;
            SageLink.Changed -= StateChanged;
            _operation = null;
        }
    }

    /// <summary>New chat: the next message starts a new Sage conversation.</summary>
    public void Reset()
    {
        if (_operation is { } op) _ = SageLink.CallAsync("chat.cancel", new { operationId = op }).ContinueWith(_ => { }, TaskScheduler.Default);
        ConversationId = null;
        _fresh = true;
    }

    async Task<string> ConversationAsync()
    {
        if (ConversationId != null) return ConversationId;
        if (!_fresh)
        {
            var list = await SageLink.CallAsync("conversations.list");
            if (list.ValueKind == JsonValueKind.Array)
                foreach (var item in list.EnumerateArray())
                {
                    var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    if (title.StartsWith(DiscordContactPrefix, StringComparison.Ordinal) || title == DiscordBridge) continue;
                    if (item.TryGetProperty("isEphemeral", out var eph) && eph.ValueKind == JsonValueKind.True) continue;
                    return ConversationId = item.GetProperty("id").GetString()!;
                }
        }
        var created = await SageLink.CallAsync("conversations.create", new { title = "Notch", confidential = false, ephemeral = false });
        _fresh = false;
        return ConversationId = created.GetProperty("id").GetString()!;
    }

    /// <summary>The last messages of the current conversation (picking it first if needed), oldest first.</summary>
    public async Task<List<(bool Mine, string Text)>> HistoryAsync(int count = 30)
    {
        var result = new List<(bool, string)>();
        if (!SageLink.Connected) return result;
        var conversation = await ConversationAsync();
        var messages = await SageLink.CallAsync("chat.history", new { conversationId = conversation });
        if (messages.ValueKind != JsonValueKind.Array) return result;
        foreach (var m in messages.EnumerateArray())
        {
            var role = m.TryGetProperty("role", out var r) && r.TryGetInt32(out var n) ? n
                : r.ValueKind == JsonValueKind.String ? (r.GetString() == "User" ? 1 : r.GetString() == "Assistant" ? 2 : 0) : 0;
            var content = m.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            if ((role == 1 || role == 2) && content.Trim().Length > 0) result.Add((role == 1, content.Trim()));
        }
        return result.Count > count ? result[^count..] : result;
    }

    /// <summary>What Sage is up to, in a few words, for the status line under a reply.</summary>
    public static string? Describe(string state, string? detail) => state switch
    {
        "Thinking" => "Thinking…",
        "Acting" => detail != null ? $"Using {Tidy(detail)}…" : "Working…",
        "Researching" => detail != null ? $"Looking up {Tidy(detail)}…" : "Researching…",
        "Observing" => "Looking at your screen…",
        _ => null
    };

    static string Tidy(string tool) => tool.Replace('_', ' ').Replace('.', ' ');
}
