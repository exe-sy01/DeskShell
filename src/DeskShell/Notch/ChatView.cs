using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskShell.Claude;

namespace DeskShell.Notch;

/// <summary>Who a <see cref="ChatView"/> talks to.</summary>
interface IChatBackend
{
    /// <summary>"Claude", "Sage": used in the message box's placeholder.</summary>
    string Name { get; }
    bool Available { get; }
    string UnavailableMessage { get; }
    string Welcome { get; }
    bool Busy { get; }
    /// <summary>Sends a message and streams the reply. Throws <see cref="OperationCanceledException"/> when stopped, any other exception with a message fit to show.</summary>
    Task SendAsync(string prompt, Action<string> onDelta, Action<string> onStatus, CancellationToken ct);
    /// <summary>Forgets the conversation (New chat).</summary>
    void Reset();
}

/// <summary>The Claude CLI chat behind the Claude tab.</summary>
sealed class ClaudeChatBackend : IChatBackend
{
    readonly ClaudeChat _chat = new();
    public string Name => "Claude";
    public bool Available => ClaudeChat.Available;
    public string UnavailableMessage => ClaudeChat.NotInstalledMessage;
    public string Welcome => "Ask Claude anything. It uses your Claude subscription and can't see or change your files.";
    public bool Busy => _chat.Busy;
    public Task SendAsync(string prompt, Action<string> onDelta, Action<string> onStatus, CancellationToken ct) => _chat.SendAsync(prompt, onDelta, onStatus, ct);
    public void Reset() => _chat.Reset();
}

/// <summary>
/// A notch chat: a scrolling transcript with a message box under it.
/// Your messages sit right in rounded bubbles; replies are plain, selectable text so they can be copied.
/// </summary>
sealed class ChatView : Grid
{
    const string SendGlyph = "";
    const string StopGlyph = "";

    readonly IChatBackend _chat;
    readonly Action _activate;
    readonly StackPanel _log = new() { Margin = new Thickness(0, 0, 6, 0) };
    readonly ScrollViewer _scroll;
    readonly TextBox _input;
    readonly Button _send;
    readonly TextBlock _sendGlyph;
    CancellationTokenSource? _cts;
    bool _busy;
    /// <summary>Bumped by New chat, so a reply still finishing from the old conversation can't write into the new one.</summary>
    int _conversation;
    /// <summary>Whether to follow new text down; turned off when you scroll up to read, back on when you return to the bottom.</summary>
    bool _stick = true;

    /// <param name="activate">Lets the notch take keyboard focus; called when the message box is clicked or focused.</param>
    public ChatView(IChatBackend backend, Action activate)
    {
        _chat = backend;
        _activate = activate;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _scroll = new ScrollViewer
        {
            Content = _log,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false
        };
        _scroll.ScrollChanged += OnScrollChanged;
        Children.Add(_scroll);

        var fieldHost = UI.Field($"Ask {backend.Name}…", out _input);
        _input.AcceptsReturn = true;
        _input.TextWrapping = TextWrapping.Wrap;
        _input.MaxHeight = 96;
        _input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _input.PreviewMouseLeftButtonDown += (_, _) => _activate();
        _input.GotKeyboardFocus += (_, _) => _activate();
        _input.PreviewKeyDown += (_, e) =>
        {
            // Enter sends; Shift+Enter falls through to AcceptsReturn and adds a line.
            if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
            e.Handled = true;
            if (!_busy) Send();
        };

        var newChat = UI.Round("", 30, NewChat, "New chat");
        newChat.Margin = new Thickness(0, 0, 8, 0);
        newChat.VerticalAlignment = VerticalAlignment.Bottom;
        _send = UI.Round(SendGlyph, 30, SendOrStop, "Send", light: true);
        _send.Margin = new Thickness(8, 0, 0, 0);
        _send.VerticalAlignment = VerticalAlignment.Bottom;
        _sendGlyph = (TextBlock)((Border)_send.Content).Child;

        var row = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        SetColumn(newChat, 0);
        SetColumn(fieldHost, 1);
        SetColumn(_send, 2);
        row.Children.Add(newChat);
        row.Children.Add(fieldHost);
        row.Children.Add(_send);
        SetRow(row, 1);
        Children.Add(row);

        ShowWelcome();
    }

    /// <summary>Puts the cursor in the message box (and lets the notch take the keyboard).</summary>
    public void FocusInput()
    {
        _activate();
        _input.Focus();
        Keyboard.Focus(_input);
    }

    void ShowWelcome()
    {
        var welcome = Note(_chat.Available ? _chat.Welcome : _chat.UnavailableMessage);
        welcome.Margin = new Thickness(2, 4, 0, 0);
        welcome.Tag = "welcome";
        _log.Children.Add(welcome);
    }

    void DropWelcome()
    {
        if (_log.Children.Count == 1 && _log.Children[0] is FrameworkElement { Tag: "welcome" }) _log.Children.Clear();
    }

    /// <summary>Shows the welcome line again if the chat is empty (say, once the backend becomes available).</summary>
    public void RefreshWelcome()
    {
        if (_log.Children.Count > 1 || _busy) return;
        _log.Children.Clear();
        ShowWelcome();
    }

    /// <summary>Replaces the transcript with earlier messages (true = yours).</summary>
    public void ShowHistory(IEnumerable<(bool Mine, string Text)> messages)
    {
        if (_busy) return;
        _log.Children.Clear();
        foreach (var (mine, text) in messages)
        {
            if (mine) _log.Children.Add(UserBubble(text));
            else
            {
                var reply = ReplyBox();
                reply.Text = ChatText.Tidy(text);
                _log.Children.Add(reply);
            }
        }
        if (_log.Children.Count == 0) ShowWelcome();
        _stick = true;
        _scroll.ScrollToEnd();
    }

    /// <summary>
    /// A message that started somewhere else (said out loud, say): shows it, and returns a sink for its reply.
    /// Call the first action with each piece of the reply and the second when it ends (with an error, or null).
    /// </summary>
    public (Action<string> Delta, Action<string?> Done) AddExternal(string text)
    {
        DropWelcome();
        if (text.Length > 0) _log.Children.Add(UserBubble(text));
        var reply = ReplyBox();
        reply.Visibility = Visibility.Collapsed;
        var status = Note("Thinking…");
        _log.Children.Add(reply);
        _log.Children.Add(status);
        _stick = true;
        _scroll.ScrollToEnd();
        var conversation = _conversation;
        var raw = new StringBuilder();
        return (delta =>
        {
            if (conversation != _conversation) return;
            raw.Append(delta);
            reply.Text = ChatText.Tidy(raw.ToString());
            reply.Visibility = Visibility.Visible;
            status.Visibility = Visibility.Collapsed;
        }, error =>
        {
            if (conversation != _conversation) return;
            if (error != null) Show(status, error);
            else if (raw.Length == 0) Show(status, "No reply.");
            else _log.Children.Remove(status);
        });
    }

    void SendOrStop()
    {
        if (_busy) _cts?.Cancel();
        else Send();
    }

    /// <summary>Clears the transcript and starts a fresh conversation, stopping any reply that is still coming.</summary>
    void NewChat()
    {
        _conversation++;
        _cts?.Cancel();
        _chat.Reset();
        _log.Children.Clear();
        SetBusy(false);
        _stick = true;
        ShowWelcome();
    }

    async void Send()
    {
        var text = _input.Text.Trim();
        if (text.Length == 0 || _busy) return;

        DropWelcome();
        if (!_chat.Available)
        {
            _log.Children.Add(Note(_chat.UnavailableMessage));
            return;
        }

        _input.Clear();
        _log.Children.Add(UserBubble(text));
        var reply = ReplyBox();
        reply.Visibility = Visibility.Collapsed;
        var status = Note("Thinking…");
        _log.Children.Add(reply);
        _log.Children.Add(status);
        _stick = true;
        _scroll.ScrollToEnd();

        var conversation = _conversation;
        var cts = _cts = new CancellationTokenSource();
        var raw = new StringBuilder();
        SetBusy(true);
        try
        {
            // A New chat a moment ago may still be waiting for the old process to exit.
            for (var i = 0; i < 40 && _chat.Busy; i++) await Task.Delay(50, cts.Token);

            await _chat.SendAsync(text,
                delta =>
                {
                    if (conversation != _conversation) return;
                    raw.Append(delta);
                    reply.Text = ChatText.Tidy(raw.ToString());
                    reply.Visibility = Visibility.Visible;
                    status.Visibility = Visibility.Collapsed;
                },
                note =>
                {
                    if (conversation != _conversation) return;
                    status.Text = note;
                    status.Visibility = Visibility.Visible;
                },
                cts.Token);

            if (conversation != _conversation) return;
            if (raw.Length == 0) Show(status, "No reply.");
            else _log.Children.Remove(status);
        }
        catch (OperationCanceledException)
        {
            if (conversation == _conversation) Show(status, "Stopped.");
        }
        catch (Exception e)
        {
            if (conversation == _conversation) Show(status, e.Message);
        }
        finally
        {
            if (_cts == cts) _cts = null;
            cts.Dispose();
            if (conversation == _conversation) SetBusy(false);
        }
    }

    void Show(TextBlock status, string text)
    {
        status.Text = text;
        status.Visibility = Visibility.Visible;
    }

    /// <summary>The send button turns into a stop button while a reply streams.</summary>
    void SetBusy(bool busy)
    {
        _busy = busy;
        _sendGlyph.Text = busy ? StopGlyph : SendGlyph;
        _send.ToolTip = busy ? "Stop" : "Send";
        System.Windows.Automation.AutomationProperties.SetName(_send, busy ? "Stop" : "Send");
    }

    /// <summary>Follows the bottom while text arrives, unless you've scrolled up to read.</summary>
    void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
            _stick = _scroll.VerticalOffset >= _scroll.ScrollableHeight - 8;
        else if (_stick)
            _scroll.ScrollToEnd();
    }

    static Border UserBubble(string text) => new()
    {
        Background = UI.Raised,
        CornerRadius = new CornerRadius(14),
        Padding = new Thickness(12, 7, 12, 8),
        HorizontalAlignment = HorizontalAlignment.Right,
        MaxWidth = 320,
        Margin = new Thickness(48, 6, 0, 4),
        Child = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = UI.Text,
            FontFamily = UI.Font,
            FontSize = 12.5
        }
    };

    /// <summary>Claude's reply: a borderless read-only text box, so it looks like text but can be selected and copied.</summary>
    TextBox ReplyBox()
    {
        var box = new TextBox
        {
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = UI.Text,
            CaretBrush = UI.Text,
            SelectionBrush = UI.Accent,
            FontFamily = UI.Font,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0),
            Margin = new Thickness(2, 4, 24, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Template = BareTextBox
        };
        // Copying needs the keyboard (Ctrl+C), which the notch only has once activated.
        box.PreviewMouseLeftButtonDown += (_, _) => _activate();
        // A text box eats the mouse wheel even when it has nothing to scroll; hand it to the transcript.
        box.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - e.Delta / 3.0);
        };
        return box;
    }

    static TextBlock Note(string text) => new()
    {
        Text = text,
        Foreground = UI.Dim,
        FontFamily = UI.Font,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(2, 2, 24, 6)
    };

    /// <summary>A text box template with nothing but the text: no border, hover or focus chrome.</summary>
    static readonly ControlTemplate BareTextBox = MakeBareTextBox();

    static ControlTemplate MakeBareTextBox()
    {
        var t = new ControlTemplate(typeof(TextBox));
        var host = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
        host.SetValue(ScrollViewer.FocusableProperty, false);
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        t.VisualTree = host;
        t.Seal();
        return t;
    }
}

/// <summary>
/// Light clean-up of Claude's markdown for plain text: drops heading marks and <c>**bold**</c> markers and
/// turns list dashes into bullets. Code (fenced blocks and <c>`inline`</c>) is left exactly as written.
/// </summary>
static class ChatText
{
    static readonly Regex Bold = new(@"(?<!\w)\*\*(?=\S)(.+?)(?<=\S)\*\*(?!\w)", RegexOptions.Compiled);
    static readonly Regex Heading = new(@"^\s{0,3}#{1,6}\s+", RegexOptions.Compiled);
    static readonly Regex Bullet = new(@"^(\s*)[-*+]\s+", RegexOptions.Compiled);

    public static string Tidy(string markdown)
    {
        var lines = markdown.Split('\n');
        var sb = new StringBuilder(markdown.Length);
        var fenced = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) fenced = !fenced;
            else if (!fenced)
            {
                line = Heading.Replace(line, "");
                line = Bullet.Replace(line, "$1• ");
                line = OutsideCode(line);
            }
            sb.Append(line);
            if (i < lines.Length - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Removes bold markers only between backticks' code spans (even-numbered pieces).</summary>
    static string OutsideCode(string line)
    {
        if (!line.Contains("**", StringComparison.Ordinal)) return line;
        var parts = line.Split('`');
        for (var i = 0; i < parts.Length; i += 2) parts[i] = Bold.Replace(parts[i], "$1");
        return string.Join('`', parts);
    }
}
