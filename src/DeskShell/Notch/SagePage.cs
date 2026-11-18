using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskShell.Sage;

namespace DeskShell.Notch;

/// <summary>
/// The notch's Sage tab: Sage's orb and what it's doing, its voice switches, anything it wants your OK for,
/// and a chat with it on the right (things you say out loud show up there too). Sage itself keeps running
/// as its own app; "Open Sage" brings up its full window.
/// </summary>
sealed class SagePage(NotchContext c) : NotchPage(c)
{
    public override string Id => "sage";
    public override string Title => SageLink.Name;
    public override string Glyph => "";

    readonly SageOrb _orb = new(64) { Margin = new Thickness(0, 0, 4, 0) };
    readonly TextBlock _headline = UI.Label("", 15, bold: true, font: UI.Display);
    readonly TextBlock _subline = UI.Label("", 12, brush: UI.Dim);
    readonly StackPanel _list = new();
    readonly SageChat _backend = new();
    readonly Dictionary<string, Reply> _voice = [];
    ChatView? _chat;
    bool _listening;
    bool _historyShown;

    sealed class Reply(Action<string> delta, Action<string?> done)
    {
        public bool GotText;
        public void Delta(string text) { GotText = true; delta(text); }
        public void Done(string? error) => done(error);
    }

    protected override UIElement Build()
    {
        var left = new DockPanel { Width = 300, Margin = new Thickness(0, 0, 12, 0) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        head.Children.Add(_orb);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Width = 220 };
        words.Children.Add(_headline);
        words.Children.Add(_subline);
        _subline.TextWrapping = TextWrapping.Wrap;
        _subline.MaxHeight = 34;
        head.Children.Add(words);
        DockPanel.SetDock(head, Dock.Top);
        left.Children.Add(head);
        left.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        _chat = new ChatView(_backend, Context.Activate);
        var chatCard = new Border
        {
            Background = UI.Card, BorderBrush = UI.CardEdge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18),
            Padding = new Thickness(12, 10, 12, 10), Child = _chat
        };
        var page = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        page.Children.Add(left);
        page.Children.Add(chatCard);
        return page;
    }

    public override void Refresh()
    {
        if (!_listening)
        {
            _listening = true;
            SageLink.Changed += OnChanged;
            SageLink.Level += level => _orb.SetLevel(level);
            SageLink.VoiceChatStarted += OnVoiceChat;
            SageLink.ChatDelta += (op, text) => { if (_voice.TryGetValue(op, out var r)) r.Delta(text); };
            SageLink.ChatCompleted += (op, content) =>
            {
                if (!_voice.Remove(op, out var r)) return;
                if (!r.GotText && content.Length > 0) r.Delta(content);
                r.Done(null);
            };
            SageLink.ChatFailed += (op, error, cancelled) => { if (_voice.Remove(op, out var r)) r.Done(cancelled ? "Stopped." : error); };
        }
        Draw();
        if (SageLink.Connected && !_historyShown) ShowHistory();
    }

    void OnChanged()
    {
        if (!SageLink.Connected) _historyShown = false;
        if (_list.IsVisible) Refresh();
        else _orb.Show(SageLink.State, SageLink.Approvals.Count > 0, SageLink.Speaking);
    }

    async void ShowHistory()
    {
        _historyShown = true;
        try
        {
            var history = await _backend.HistoryAsync();
            if (!_backend.Busy && _voice.Count == 0) _chat?.ShowHistory(history);
        }
        catch (Exception ex)
        {
            Log.Info($"Couldn't load Sage's chat: {ex.Message}");
            _chat?.RefreshWelcome();
        }
    }

    /// <summary>Something you said out loud: it and Sage's answer show in the chat.</summary>
    void OnVoiceChat(string op, string conversation, string text)
    {
        if (_chat == null || op.Length == 0) return;
        if (conversation.Length > 0 && conversation != _backend.ConversationId)
        {
            _backend.ConversationId = conversation;
            _historyShown = false;
        }
        var (delta, done) = _chat.AddExternal(text);
        _voice[op] = new Reply(delta, done);
    }

    void Draw()
    {
        var name = SageLink.Name;
        var asking = SageLink.Approvals.Count;
        _orb.Show(SageLink.State, asking > 0, SageLink.Speaking);
        (_headline.Text, _subline.Text) =
            !SageLink.Installed ? ($"{name} isn't installed", "Install PCAssistant to talk to it here")
            : !SageLink.Connected ? ($"{name} is asleep", "It isn't running right now")
            : asking > 0 ? ($"{name} needs your OK", asking == 1 ? SageLink.Approvals[0].Summary : $"{asking} requests waiting")
            : SageLink.Speaking ? ("Talking", "Tap the stop button to hush")
            : SageLink.State switch
            {
                "Listening" => ("Listening…", "Go ahead, I'm all ears"),
                "Thinking" => ("Thinking…", ""),
                "Acting" => ("Working on it", SageChat.Describe("Acting", SageLink.Detail)?.TrimEnd('…') ?? ""),
                "Researching" => ("Researching", SageChat.Describe("Researching", SageLink.Detail)?.TrimEnd('…') ?? ""),
                "Observing" => ("Looking", "At your screen"),
                "Error" => ("Something went wrong", SageLink.Detail ?? "Check Sage's own window"),
                _ => ("Ready", SageLink.WakeEnabled ? $"Say \"{name}\", or type on the right" : "Press the mic, or type on the right")
            };
        _chat?.RefreshWelcome();

        _list.Children.Clear();
        foreach (var approval in SageLink.Approvals) _list.Children.Add(ApprovalCard(approval));

        if (!SageLink.Connected)
        {
            if (SageLink.Installed)
            {
                var start = UI.Pill($"Start {name}", () =>
                {
                    if (SageLink.StartSage()) _subline.Text = "Starting…";
                }, "");
                start.HorizontalAlignment = HorizontalAlignment.Left;
                _list.Children.Add(start);
            }
            return;
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        buttons.Children.Add(Spaced(UI.Round("", 34, () => Try(SageLink.ListenAsync()), "Talk to " + name, light: true)));
        if (SageLink.Speaking) buttons.Children.Add(Spaced(UI.Round("", 34, () => Try(SageLink.HushAsync()), "Stop talking")));
        buttons.Children.Add(Spaced(UI.Round("", 34, () =>
        {
            if (SageLink.OpenWindow()) Context.Collapse();
            else _subline.Text = $"Couldn't open {name}'s window";
        }, $"Open {name}'s window")));
        _list.Children.Add(buttons);

        var switches = new WrapPanel();
        switches.Children.Add(Chip("Wake word", "", SageLink.WakeEnabled, on => SageLink.SetWakeAsync(on)));
        switches.Children.Add(Chip("Speak replies", "", SageLink.SpeakEnabled, on => SageLink.SetSpeakAsync(on)));
        switches.Children.Add(Chip("Hide window at startup", "", SageLink.StartHidden, on => SageLink.SetStartHiddenAsync(on)));
        _list.Children.Add(switches);
    }

    static FrameworkElement Spaced(FrameworkElement e)
    {
        e.Margin = new Thickness(0, 0, 8, 0);
        return e;
    }

    async void Try(Task task)
    {
        try { await task; }
        catch (Exception ex) { _subline.Text = ex.Message; }
    }

    /// <summary>An on/off pill: ringed in the accent colour when on.</summary>
    Button Chip(string text, string glyph, bool on, Func<bool, Task> set)
    {
        var button = UI.Pill(text, () => Try(set(!on)), glyph);
        button.Margin = new Thickness(0, 0, 6, 6);
        if (button.Content is Border bg)
        {
            bg.BorderThickness = new Thickness(1.5);
            bg.BorderBrush = on ? Context.Accent() : Brushes.Transparent;
            bg.Padding = new Thickness(11.5, 4.5, 11.5, 4.5);
        }
        System.Windows.Automation.AutomationProperties.SetName(button, $"{text} {(on ? "on" : "off")}");
        return button;
    }

    UIElement ApprovalCard(SageApproval approval)
    {
        var stack = new StackPanel();
        var (risk, riskBrush) = approval.Risk switch
        {
            SageRisk.Elevated => ("needs admin", UI.Frozen(SageOrb.Trouble)),
            SageRisk.Destructive => ("can't be undone", UI.Frozen(SageOrb.Trouble)),
            SageRisk.ReadOnly => ("read only", UI.Dim),
            _ => ("can be undone", UI.Dim)
        };
        var top = new DockPanel();
        var riskLabel = UI.Label(risk, 11, brush: riskBrush);
        DockPanel.SetDock(riskLabel, Dock.Right);
        top.Children.Add(riskLabel);
        top.Children.Add(UI.Label($"{SageLink.Name} wants to", 11.5, bold: true, brush: UI.Dim));
        stack.Children.Add(top);
        stack.Children.Add(new TextBlock
        {
            Text = approval.Summary, Foreground = UI.Text, FontFamily = UI.Font, FontSize = 13, TextWrapping = TextWrapping.Wrap,
            MaxHeight = 54, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 2)
        });
        if (approval.Target.Length > 0 && approval.Target != approval.Summary)
        {
            var target = UI.Label(approval.Target, 11, brush: UI.Dim);
            target.TextTrimming = TextTrimming.CharacterEllipsis;
            target.ToolTip = approval.Target;
            stack.Children.Add(target);
        }
        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(UI.Pill("Allow", () => _ = SageLink.AnswerAsync(approval, true), ""));
        if (approval.GrantMinutes > 0)
            buttons.Children.Add(UI.Pill(approval.GrantLabel ?? $"Allow for {approval.GrantMinutes} min", () => _ = SageLink.AnswerAsync(approval, true, grant: true)));
        buttons.Children.Add(UI.Pill("Deny", () => _ = SageLink.AnswerAsync(approval, false), ""));
        foreach (var b in buttons.Children.OfType<FrameworkElement>()) b.Margin = new Thickness(0, 0, 6, 6);
        stack.Children.Add(buttons);
        return new Border
        {
            Background = UI.Card, BorderBrush = UI.Frozen(SageOrb.Ask), BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(16), Padding = new Thickness(12, 10, 10, 6), Margin = new Thickness(0, 0, 4, 8), Child = stack
        };
    }
}
