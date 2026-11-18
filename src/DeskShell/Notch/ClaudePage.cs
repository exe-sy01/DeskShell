using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskShell.Claude;

namespace DeskShell.Notch;

/// <summary>
/// The notch's Claude tab: the pet, anything Claude is waiting for you to allow, your Claude Code
/// sessions and what each is doing, and a chat with Claude on the right.
/// </summary>
sealed class ClaudePage(NotchContext c) : NotchPage(c)
{
    public override string Id => "claude";
    public override string Title => "Claude";
    public override string Glyph => "";

    readonly Pet _pet = new() { Scale = 2.6, HorizontalAlignment = HorizontalAlignment.Left };
    readonly TextBlock _headline = UI.Label("", 15, bold: true, font: UI.Display);
    readonly TextBlock _subline = UI.Label("", 12, brush: UI.Dim);
    readonly StackPanel _list = new();
    ChatView? _chat;
    bool _listening;

    protected override UIElement Build()
    {
        var left = new DockPanel { Width = 300, Margin = new Thickness(0, 0, 12, 0) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 8) };
        head.Children.Add(_pet);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), Width = 200 };
        words.Children.Add(_headline);
        words.Children.Add(_subline);
        _subline.TextWrapping = TextWrapping.Wrap;
        _subline.MaxHeight = 34;
        head.Children.Add(words);
        DockPanel.SetDock(head, Dock.Top);
        left.Children.Add(head);
        left.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        _chat = new ChatView(new ClaudeChatBackend(), Context.Activate);
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
            ClaudeHub.Changed += () => { if (_list.IsVisible) Refresh(); };
            ClaudeHub.Finished += (_, _) => { if (_pet.IsVisible) _pet.Hop(); };
        }
        var overall = ClaudeHub.Overall;
        _pet.Mood = overall switch
        {
            ClaudeState.NeedsYou => PetMood.NeedsYou,
            ClaudeState.Working => PetMood.Working,
            _ => PetMood.Idle
        };
        var working = ClaudeHub.Sessions.Count(s => s.State == ClaudeState.Working);
        (_headline.Text, _subline.Text) = overall switch
        {
            ClaudeState.NeedsYou => ("Claude needs your OK", ClaudeHub.Approvals.Count == 1 ? ClaudeHub.Approvals[0].Summary : $"{ClaudeHub.Approvals.Count} requests waiting"),
            ClaudeState.Working => (working == 1 ? "Claude is working" : $"{working} sessions working", ClaudeHub.Sessions.First(s => s.State == ClaudeState.Working).Action),
            _ when !ClaudeSetup.IsInstalled() => ("Hi!", "Connect Claude Code to see your sessions here"),
            _ when ClaudeHub.Sessions.Count == 0 => ("All quiet", "No Claude Code sessions right now"),
            _ => ("All quiet", $"{ClaudeHub.Sessions.Count} session{(ClaudeHub.Sessions.Count == 1 ? "" : "s")}, nothing running")
        };

        _list.Children.Clear();
        foreach (var approval in ClaudeHub.Approvals) _list.Children.Add(ApprovalCard(approval));
        foreach (var session in ClaudeHub.Sessions.Take(8)) _list.Children.Add(SessionRow(session));
        if (!ClaudeSetup.IsInstalled())
        {
            var note = UI.Label("Adds DeskShell to Claude Code's hooks (its settings.json is backed up first). Then sessions from the desktop app, terminals and IDEs show here, and you can allow or deny their requests.", 11.5, brush: UI.Dim);
            note.TextWrapping = TextWrapping.Wrap;
            note.Margin = new Thickness(2, 4, 0, 8);
            _list.Children.Add(note);
            _list.Children.Add(UI.Pill("Connect Claude Code", Connect, ""));
        }
    }

    void Connect()
    {
        try
        {
            ClaudeSetup.Install(Installer.IsInstalled ? Installer.InstalledExe : Environment.ProcessPath!);
            _subline.Text = "Connected. New sessions show up here.";
        }
        catch (Exception ex)
        {
            Log.Error("Couldn't add Claude Code hooks", ex);
            _subline.Text = "Couldn't connect: " + ex.Message;
        }
        Refresh();
    }

    UIElement ApprovalCard(ClaudeApproval approval)
    {
        var stack = new StackPanel();
        var title = UI.Label($"{approval.Project} wants to", 11.5, bold: true, brush: UI.Dim);
        stack.Children.Add(title);
        var what = new TextBlock { Text = approval.Summary, Foreground = UI.Text, FontFamily = UI.Font, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxHeight = 54, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 8) };
        stack.Children.Add(what);
        var buttons = new WrapPanel();
        buttons.Children.Add(UI.Pill("Allow", () => ClaudeHub.Answer(approval, ClaudeAnswer.Allow), ""));
        buttons.Children.Add(UI.Pill("Always", () => ClaudeHub.Answer(approval, ClaudeAnswer.AlwaysAllow)));
        buttons.Children.Add(UI.Pill("Deny", () => ClaudeHub.Answer(approval, ClaudeAnswer.Deny), ""));
        buttons.Children.Add(UI.Pill("In Claude", () => ClaudeHub.Answer(approval, ClaudeAnswer.AnswerInClaude)));
        foreach (var b in buttons.Children.OfType<FrameworkElement>()) b.Margin = new Thickness(0, 0, 6, 6);
        stack.Children.Add(buttons);
        return new Border
        {
            Background = UI.Card, BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x4A)), BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(16), Padding = new Thickness(12, 10, 10, 6), Margin = new Thickness(0, 0, 4, 8), Child = stack
        };
    }

    static UIElement SessionRow(ClaudeSession session)
    {
        var (dot, label) = session.State switch
        {
            ClaudeState.Working => (UI.Accent, "Working"),
            ClaudeState.NeedsYou => (UI.Frozen(Color.FromRgb(0xFF, 0xD2, 0x4A)), "Needs you"),
            ClaudeState.Done => (UI.Frozen(Color.FromRgb(0x3B, 0xD1, 0x6F)), "Done"),
            _ => (UI.Faint, "Idle")
        };
        var top = new DockPanel();
        var state = UI.Label(label, 11, brush: UI.Dim);
        DockPanel.SetDock(state, Dock.Right);
        top.Children.Add(state);
        var name = new StackPanel { Orientation = Orientation.Horizontal };
        name.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Background = dot, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        name.Children.Add(UI.Label(session.Project, 12.5, bold: true));
        top.Children.Add(name);
        var action = UI.Label(session.Action.Length > 0 ? session.Action : session.Prompt, 11.5, brush: UI.Dim);
        action.Margin = new Thickness(15, 2, 0, 0);
        var stack = new StackPanel { Children = { top, action } };
        if (session.Prompt.Length > 0) stack.ToolTip = session.Prompt;
        return new Border { Background = UI.Card, BorderBrush = UI.CardEdge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 4, 6), Child = stack };
    }
}
