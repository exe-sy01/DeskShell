using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DeskShell.Services;

public sealed record VoicePeer(string Id, string Name, string? Avatar, bool Speaking, bool Muted, bool Deafened);

public sealed record VoiceCall(string Channel, string? Server, IReadOnlyList<VoicePeer> Peers);

/// <summary>A DM, group DM or direct mention you haven't read yet.</summary>
public sealed record DiscordMessage(string Id, string ChannelId, string? GuildId, string Kind, string? Channel, string? Server,
    string Author, string? Avatar, string Text, bool Quiet, string ClientProcess, DateTime Arrived)
{
    /// <summary>"Alex", "Alex · Group name" or "Alex · #general".</summary>
    public string Title => Kind switch
    {
        "dm" => Author,
        "group" => $"{Author} · {Channel ?? "Group"}",
        _ => Channel != null ? $"{Author} · #{Channel}" : Author
    };
}

/// <summary>
/// What's happening in Discord, as reported by the AIChat Vencord plugin (deskshell.ts) over a
/// WebSocket to 127.0.0.1:47390: the voice call you're in (who's speaking, who's muted) and DMs
/// and mentions you haven't read. Only Discord's own pages may connect (checked by their Origin),
/// so a website open in a browser can't feed it. Nothing is sent back, and messages are only kept
/// in memory until they're read.
/// </summary>
public static class DiscordLink
{
    public const int Port = 47390;
    const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    static readonly string[] AllowedOrigins = ["https://discord.com", "https://canary.discord.com", "https://ptb.discord.com"];

    static TcpListener? _listener;
    static CancellationTokenSource? _stop;
    static Dispatcher? _ui;
    static int _nextClient;
    // Each connected Discord (stable, PTB, Canary) reports its own call; the latest one wins.
    static readonly Dictionary<int, (VoiceCall? Call, long At)> _clients = new();
    // Discord re-sends the same call many times a second (any voice change in the server fires it); repeats are ignored.
    static readonly Dictionary<int, string> _lastVoiceFrame = new();
    // Which Discord each connection is (its process name), to open messages in the right one
    static readonly Dictionary<int, string> _clientProcesses = new();
    const int MaxMessages = 50;
    static readonly List<DiscordMessage> _messages = new();
    // Several Discords signed in to one account each report the same message
    static readonly HashSet<string> _seenMessages = new();
    static readonly Queue<string> _seenOrder = new();
    static readonly Dictionary<string, ImageSource> AvatarCache = new();

    /// <summary>The call you're in, or null.</summary>
    public static VoiceCall? Call { get; private set; }

    /// <summary>Raised on the UI thread when the call changes (joined, left, someone speaks…).</summary>
    public static event Action? CallChanged;

    /// <summary>Unread messages, newest first (UI thread only).</summary>
    public static IReadOnlyList<DiscordMessage> Messages => _messages;

    /// <summary>A new message, raised on the UI thread after <see cref="Messages"/> changes.</summary>
    public static event Action<DiscordMessage>? MessageArrived;

    /// <summary>Messages were read, deleted or added.</summary>
    public static event Action? MessagesChanged;

    public static void Start()
    {
        _ui = Dispatcher.CurrentDispatcher;
        _stop = new CancellationTokenSource();
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
        }
        catch (SocketException ex)
        {
            Log.Error($"Can't listen for Discord voice calls on port {Port}", ex);
            _listener = null;
            return;
        }
        _ = AcceptLoopAsync(_stop.Token);
    }

    public static void Stop()
    {
        _stop?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    static async Task AcceptLoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested && _listener != null)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(stop); }
            catch { return; }
            var id = Interlocked.Increment(ref _nextClient);
            _ = Task.Run(() => ServeAsync(client, id, stop));
        }
    }

    static async Task ServeAsync(TcpClient client, int id, CancellationToken stop)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var origin = await HandshakeAsync(stream, stop);
                if (origin == null) return;
                lock (_clients) _clientProcesses[id] = ClientProcess(origin);
                Log.Info("Discord connected");
                await ReadMessagesAsync(stream, text => OnMessage(id, text), stop);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Discord closed or reloaded
            }
            catch (Exception ex)
            {
                Log.Error("Discord voice connection failed", ex);
            }
            finally
            {
                lock (_clients)
                {
                    _clients.Remove(id);
                    _lastVoiceFrame.Remove(id);
                    _clientProcesses.Remove(id);
                }
                Publish();
            }
        }
    }

    /// <summary>The WebSocket opening handshake (RFC 6455), refusing anything that isn't Discord.</summary>
    static async Task<string?> HandshakeAsync(NetworkStream stream, CancellationToken stop)
    {
        var buffer = new byte[8192];
        var length = 0;
        while (true)
        {
            if (length == buffer.Length) return null;
            var read = await stream.ReadAsync(buffer.AsMemory(length), stop);
            if (read == 0) return null;
            length += read;
            if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n")) break;
        }
        var lines = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n");
        string? Header(string name) => lines.Skip(1)
            .Select(l => l.Split(':', 2))
            .Where(p => p.Length == 2 && p[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(p => p[1].Trim())
            .FirstOrDefault();

        var origin = Header("Origin");
        var key = Header("Sec-WebSocket-Key");
        if (key == null || origin == null || !AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n"), stop);
            return null;
        }
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), stop);
        return origin;
    }

    /// <summary>Reads WebSocket frames, answering pings and calling <paramref name="onText"/> for each text message.</summary>
    static async Task ReadMessagesAsync(NetworkStream stream, Action<string> onText, CancellationToken stop)
    {
        var message = new MemoryStream();
        var head = new byte[14];
        while (!stop.IsCancellationRequested)
        {
            await ReadExactAsync(stream, head, 2, stop);
            var fin = (head[0] & 0x80) != 0;
            var opcode = head[0] & 0x0F;
            var masked = (head[1] & 0x80) != 0;
            long length = head[1] & 0x7F;
            if (length == 126)
            {
                await ReadExactAsync(stream, head, 2, stop);
                length = (head[0] << 8) | head[1];
            }
            else if (length == 127)
            {
                await ReadExactAsync(stream, head, 8, stop);
                length = 0;
                for (var i = 0; i < 8; i++) length = (length << 8) | head[i];
            }
            if (length > 1_000_000) return; // far more than a call ever needs
            var mask = new byte[4];
            if (masked) await ReadExactAsync(stream, mask, 4, stop);
            var payload = new byte[length];
            await ReadExactAsync(stream, payload, (int)length, stop);
            if (masked)
                for (var i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];

            switch (opcode)
            {
                case 0x0 or 0x1: // continuation / text
                    message.Write(payload);
                    if (message.Length > 1_000_000) return;
                    if (fin)
                    {
                        onText(Encoding.UTF8.GetString(message.ToArray()));
                        message.SetLength(0);
                    }
                    break;
                case 0x8: // close
                    await stream.WriteAsync(new byte[] { 0x88, 0x00 }, stop);
                    return;
                case 0x9: // ping → pong with the same payload (server frames are unmasked)
                    if (payload.Length <= 125)
                    {
                        var pong = new byte[2 + payload.Length];
                        pong[0] = 0x8A;
                        pong[1] = (byte)payload.Length;
                        payload.CopyTo(pong, 2);
                        await stream.WriteAsync(pong, stop);
                    }
                    break;
            }
        }
    }

    static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken stop)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), stop);
            if (read == 0) throw new IOException("closed");
            offset += read;
        }
    }

    static void OnMessage(int client, string text)
    {
        VoiceCall? call;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type)) return;
            switch (type.ValueKind == JsonValueKind.String ? type.GetString() : null)
            {
                case "voice":
                    break;
                case "message":
                    OnDiscordMessage(client, root);
                    return;
                case "message-read":
                    // Read the id now: the removal runs later, after this JSON is gone
                    var readChannel = Text(root, "channelId");
                    RemoveMessages(m => m.ChannelId == readChannel);
                    return;
                case "message-deleted":
                    var deletedId = Text(root, "id");
                    RemoveMessages(m => m.Id == deletedId);
                    return;
                default:
                    return;
            }
            lock (_clients)
            {
                if (_lastVoiceFrame.TryGetValue(client, out var last) && last == text) return;
                _lastVoiceFrame[client] = text;
            }
            call = ParseCall(root);
        }
        catch (JsonException)
        {
            return;
        }
        lock (_clients) _clients[client] = (call, Environment.TickCount64);
        Publish();
    }

    // ---------- messages ----------

    static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool IsSnowflake(string? id) => !string.IsNullOrEmpty(id) && id.Length <= 24 && id.All(char.IsAsciiDigit);

    /// <summary>Discord's own picture servers only; a message can't make DeskShell fetch from anywhere else.</summary>
    static string? SafeAvatar(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host is "cdn.discordapp.com" or "media.discordapp.net" || uri.Host.EndsWith(".discordapp.com", StringComparison.OrdinalIgnoreCase))
            ? url : null;

    static string ClientProcess(string origin) => origin.ToLowerInvariant() switch
    {
        "https://canary.discord.com" => "DiscordCanary",
        "https://ptb.discord.com" => "DiscordPTB",
        _ => "Discord"
    };

    static void OnDiscordMessage(int client, JsonElement root)
    {
        var id = Text(root, "id");
        var channel = Text(root, "channelId");
        var guild = Text(root, "guildId");
        if (!IsSnowflake(id) || !IsSnowflake(channel) || (guild != null && !IsSnowflake(guild))) return;

        var author = root.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
        var isObject = author.ValueKind == JsonValueKind.Object;
        var text = (Text(root, "text") ?? "").ReplaceLineEndings(" ");
        string process;
        lock (_clients) process = _clientProcesses.GetValueOrDefault(client, "Discord");
        var message = new DiscordMessage(id!, channel!, guild, Text(root, "kind") ?? "dm", Text(root, "channel"), Text(root, "server"),
            (isObject ? Text(author, "name") : null) ?? "Someone", isObject ? SafeAvatar(Text(author, "avatar")) : null,
            text.Length > 300 ? text[..300] : text, root.TryGetProperty("quiet", out var q) && q.ValueKind == JsonValueKind.True, process, DateTime.Now);

        _ui?.InvokeAsync(() =>
        {
            if (!_seenMessages.Add(message.Id)) return;
            _seenOrder.Enqueue(message.Id);
            while (_seenOrder.Count > 500) _seenMessages.Remove(_seenOrder.Dequeue());
            _messages.Insert(0, message);
            if (_messages.Count > MaxMessages) _messages.RemoveAt(_messages.Count - 1);
            MessageArrived?.Invoke(message);
            MessagesChanged?.Invoke();
        });
    }

    static void RemoveMessages(Predicate<DiscordMessage> match) =>
        _ui?.InvokeAsync(() =>
        {
            if (_messages.RemoveAll(match) > 0) MessagesChanged?.Invoke();
        });

    /// <summary>
    /// Opens a message in the Discord it came from: handing that copy a discord:// link brings it
    /// forward at the message. Windows only knows one Discord as the link's owner (whichever
    /// installed last), so that's the fallback when the right copy can't be found.
    /// </summary>
    public static void Open(DiscordMessage message)
    {
        RemoveMessages(m => m.ChannelId == message.ChannelId);
        var url = $"discord://-/channels/{message.GuildId ?? "@me"}/{message.ChannelId}/{message.Id}";
        try
        {
            var exe = RunningExe(message.ClientProcess);
            if (exe != null)
            {
                var start = new ProcessStartInfo(exe) { UseShellExecute = false };
                foreach (var arg in new[] { "--url", "--", url }) start.ArgumentList.Add(arg);
                Process.Start(start);
                return;
            }
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't open a Discord message", ex);
        }
    }

    static string? RunningExe(string processName)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is { } path) return path;
                }
                catch
                {
                    // can't read this one; try the next
                }
            }
        }
        return null;
    }

    /// <summary>A Discord profile picture, downloaded once and kept for the session.</summary>
    public static ImageSource? Avatar(string? url)
    {
        if (url == null) return null;
        if (AvatarCache.TryGetValue(url, out var cached)) return cached;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(url);
            image.DecodePixelWidth = 64;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            AvatarCache[url] = image;
            return image;
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't load a Discord avatar", ex);
            return null;
        }
    }

    // ---------- voice ----------

    static VoiceCall? ParseCall(JsonElement root)
    {
        if (!root.TryGetProperty("channel", out var channel) || channel.ValueKind != JsonValueKind.Object) return null;
        string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        var peers = new List<VoicePeer>();
        if (root.TryGetProperty("peers", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in list.EnumerateArray().Take(50))
            {
                if (p.ValueKind != JsonValueKind.Object) continue;
                var avatar = Str(p, "avatar");
                if (avatar != null && !avatar.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) avatar = null;
                peers.Add(new VoicePeer(
                    Str(p, "id") ?? "",
                    Str(p, "name") ?? "Someone",
                    avatar,
                    Bool(p, "speaking"),
                    Bool(p, "muted") || Bool(p, "selfMuted"),
                    Bool(p, "deafened") || Bool(p, "selfDeafened")));
            }
        }
        var server = root.TryGetProperty("server", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        return new VoiceCall(Str(channel, "name") ?? "Voice", server, peers);
    }

    static void Publish()
    {
        VoiceCall? call;
        lock (_clients)
            call = _clients.Values.Where(c => c.Call != null).OrderByDescending(c => c.At).Select(c => c.Call).FirstOrDefault();
        _ui?.InvokeAsync(() =>
        {
            Call = call;
            CallChanged?.Invoke();
        });
    }
}
