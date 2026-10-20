using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ManagedShell;
using ManagedShell.WindowsTasks;

namespace DeskShell.WindowManagement;

/// <summary>Every open window laid out side by side with live previews (like Mission Control).</summary>
internal sealed class OverviewWindow : ThumbnailWindow
{
    readonly ShellManager _shell;
    readonly Canvas _canvas = new();
    readonly TextBlock _hint;
    int _selected;

    public bool IsOpen { get; private set; }

    public OverviewWindow(ShellManager shell)
    {
        _shell = shell;
        _hint = new TextBlock
        {
            Text = "Click a window to switch to it  ·  arrow keys + Enter  ·  middle-click or × closes a window  ·  Esc to leave",
            FontSize = 12.5,
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 24)
        };
        var root = new Grid();
        root.Children.Add(_canvas);
        root.Children.Add(_hint);
        Content = root;
        KeyDown += OnKeyDown;
        Deactivated += (_, _) => CloseOverview();
        MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource == _canvas || e.OriginalSource == root) CloseOverview();
        };
        SizeChanged += (_, _) => { if (IsOpen) LayoutCards(); };
    }

    List<ApplicationWindow> Windows() =>
        _shell.Tasks.GroupedWindows?.OfType<ApplicationWindow>().Where(w => w.ShowInTaskbar).ToList() ?? new();

    public void Toggle()
    {
        if (IsOpen) CloseOverview();
        else OpenOverview();
    }

    public void OpenOverview()
    {
        if (IsOpen) return;
        App.Instance.HideShell();
        IsOpen = true;
        PlaceOn(ZoneManager.ScreenAtCursor().Bounds);
        Rebuild();
        Activate();
        Focus();
    }

    public void CloseOverview()
    {
        if (!IsOpen) return;
        IsOpen = false;
        ClearCards();
        _canvas.Children.Clear();
        Hide();
    }

    void Rebuild()
    {
        ClearCards();
        _canvas.Children.Clear();
        var windows = Windows();
        _hint.Text = windows.Count == 0 ? "No open windows  ·  Esc to leave" : _hint.Text;
        foreach (var w in windows) _canvas.Children.Add(MakeCard(w, 100, 100, closeButton: true).Root);
        _selected = 0;
        LayoutCards();
        Highlight(Cards.ElementAtOrDefault(_selected));
        Dispatcher.InvokeAsync(RegisterThumbnails, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void LayoutCards()
    {
        var n = Cards.Count;
        if (n == 0 || ActualWidth < 10) return;
        const double margin = 64, gap = 28, titleHeight = 30, aspect = 16 / 10.0;
        var areaW = ActualWidth - margin * 2;
        var areaH = ActualHeight - margin * 2 - 30;

        // Pick the column count that makes previews biggest.
        double bestW = 0;
        var bestCols = 1;
        for (var cols = 1; cols <= n; cols++)
        {
            var rows = (int)Math.Ceiling(n / (double)cols);
            var cellW = (areaW - gap * (cols - 1)) / cols;
            var cellH = (areaH - gap * (rows - 1)) / rows;
            var w = Math.Min(cellW, (cellH - titleHeight) * aspect);
            if (w > bestW) { bestW = w; bestCols = cols; }
        }

        var cardW = Math.Min(bestW, 640);
        var cardH = cardW / aspect + titleHeight;
        var rowsUsed = (int)Math.Ceiling(n / (double)bestCols);
        var top = margin + (areaH - (rowsUsed * cardH + (rowsUsed - 1) * gap)) / 2;

        for (var i = 0; i < n; i++)
        {
            var row = i / bestCols;
            var inRow = Math.Min(bestCols, n - row * bestCols);
            var left = margin + (areaW - (inRow * cardW + (inRow - 1) * gap)) / 2;
            var col = i % bestCols;
            var root = Cards[i].Root;
            root.Width = cardW;
            root.Height = cardH;
            Canvas.SetLeft(root, left + col * (cardW + gap));
            Canvas.SetTop(root, top + row * (cardH + gap));
        }
    }

    protected override void OnHover(Card card)
    {
        _selected = Cards.IndexOf(card);
        Highlight(card);
    }

    protected override void OnPick(Card card)
    {
        var hwnd = card.Window.Handle;
        CloseOverview();
        WmNative.Activate(hwnd);
    }

    protected override void OnWindowClosed(ApplicationWindow window) =>
        Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(350);
            if (IsOpen) Rebuild();
        });

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Cards.Count == 0)
        {
            if (e.Key == Key.Escape) CloseOverview();
            return;
        }
        switch (e.Key)
        {
            case Key.Escape: CloseOverview(); break;
            case Key.Right or Key.Tab: _selected = (_selected + 1) % Cards.Count; Highlight(Cards[_selected]); break;
            case Key.Left: _selected = (_selected - 1 + Cards.Count) % Cards.Count; Highlight(Cards[_selected]); break;
            case Key.Enter: OnPick(Cards[_selected]); break;
            default: return;
        }
        e.Handled = true;
    }
}
