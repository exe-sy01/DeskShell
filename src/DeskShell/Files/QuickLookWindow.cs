using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace DeskShell.Files;

/// <summary>
/// A big look at one file without opening it (like macOS Quick Look): a card over the file
/// explorer with the picture, text, video or song at a useful size. Space or Esc puts it away;
/// the arrow keys ask the explorer to move to the next file, which then calls
/// <see cref="ShowFor"/> again.
/// </summary>
public sealed class QuickLookWindow : Window
{
    readonly TextBlock _title;
    readonly Border _body;
    string? _path;
    CancellationTokenSource? _cts;
    MediaView? _media;
    Window? _watched;

    /// <summary>The Open button (or Enter) was pressed for this path.</summary>
    public event Action<string>? OpenRequested;

    /// <summary>An arrow key was pressed: −1 for the previous item, +1 for the next.</summary>
    public event Action<int>? NavigateRequested;

    /// <summary>The file being shown, or null while hidden.</summary>
    public string? CurrentPath => _path;

    /// <summary>True once the window has been closed (with its owner, or Alt+F4); make a new one then.</summary>
    public bool IsClosed { get; private set; }

    public QuickLookWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "Quick look";
        ThemeManager.Use(Resources);
        SetResourceReference(TextElement.FontFamilyProperty, "UiFont");
        SetResourceReference(ForegroundProperty, "TextBrush");

        // Header: name, Open, close.
        _title = new TextBlock
        {
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 12, 0)
        };
        var open = new Button { Content = "Open", Tag = "", Focusable = false, Margin = new Thickness(0, 0, 6, 0) };
        open.SetResourceReference(StyleProperty, "PillButton");
        open.Click += (_, _) => RequestOpen();
        var close = new Button { Content = PreviewBuilder.Glyph("", 14), Focusable = false, ToolTip = "Close (Esc)" };
        close.SetResourceReference(StyleProperty, "GhostButton");
        close.Click += (_, _) => HideLook();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14), LastChildFill = true };
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(open, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(open);
        header.Children.Add(_title);
        DockPanel.SetDock(header, Dock.Top);

        _body = new Border { ClipToBounds = true };
        var content = new DockPanel { LastChildFill = true };
        content.Children.Add(header);
        content.Children.Add(_body);

        var card = new Border
        {
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16),
            Child = content
        };
        card.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "EdgeBrush");

        // The card takes the middle 80% of the explorer; the dimmed rest closes it when clicked.
        var scrim = new Grid();
        scrim.SetResourceReference(Panel.BackgroundProperty, "DimBrush");
        scrim.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scrim.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8, GridUnitType.Star) });
        scrim.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scrim.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        scrim.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8, GridUnitType.Star) });
        scrim.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(card, 1);
        Grid.SetRow(card, 1);
        scrim.Children.Add(card);
        scrim.MouseLeftButtonDown += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, scrim)) return;
            e.Handled = true;
            HideLook();
        };
        Content = scrim;
    }

    /// <summary>
    /// Shows <paramref name="path"/> over <paramref name="owner"/>. The first call shows the
    /// window; later calls swap the file in place. Call on the UI thread.
    /// </summary>
    public void ShowFor(Window owner, string path)
    {
        if (IsClosed) throw new InvalidOperationException("This quick look window has been closed; make a new one.");
        if (!ReferenceEquals(Owner, owner))
        {
            Owner = owner;
            Watch(owner);
        }
        FitTo(owner);
        Load(path);
        if (!IsVisible) Show();
        Activate();
        Focus();
    }

    /// <summary>Puts the window away and lets go of the file (stops playback).</summary>
    public void HideLook()
    {
        CancelWork();
        _path = null;
        _body.Child = null;
        if (!IsVisible) return;
        Hide();
        Owner?.Activate(); // back to the file list, so the keyboard keeps working there
    }

    void Load(string path)
    {
        CancelWork();
        _path = path;
        _title.Text = PreviewBuilder.DisplayName(path);
        _body.Child = null;
        var cts = _cts = new CancellationTokenSource();
        PreviewBuilder.Run(PreviewBuilder.BuildAsync(path, PreviewScale.Large,
            element => _body.Child = element, media => _media = media, cts.Token));
    }

    void CancelWork()
    {
        _cts?.Cancel();
        _cts = null;
        _media?.Release();
        _media = null;
    }

    void RequestOpen()
    {
        if (_path != null) OpenRequested?.Invoke(_path);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Handled before anything inside (the text view would otherwise take the arrows and Space).
        switch (e.Key)
        {
            case Key.Escape:
            case Key.Space:
                HideLook();
                e.Handled = true;
                break;
            case Key.Left:
            case Key.Up:
                NavigateRequested?.Invoke(-1);
                e.Handled = true;
                break;
            case Key.Right:
            case Key.Down:
                NavigateRequested?.Invoke(1);
                e.Handled = true;
                break;
            case Key.Enter:
                RequestOpen();
                e.Handled = true;
                break;
        }
        if (!e.Handled) base.OnPreviewKeyDown(e);
    }

    // ---------- staying over the owner ----------

    /// <summary>Covers the owner exactly (its card is then 80% of the owner, centred).</summary>
    void FitTo(Window owner)
    {
        if (!owner.IsLoaded || owner.WindowState == WindowState.Minimized) return;
        var source = PresentationSource.FromVisual(owner);
        if (source?.CompositionTarget != null)
        {
            // Left/Top are the restore position for a maximized window, so ask where it really is.
            var topLeft = source.CompositionTarget.TransformFromDevice.Transform(owner.PointToScreen(new Point(0, 0)));
            Left = topLeft.X;
            Top = topLeft.Y;
        }
        else
        {
            Left = owner.Left;
            Top = owner.Top;
        }
        Width = Math.Max(200, owner.ActualWidth);
        Height = Math.Max(200, owner.ActualHeight);
    }

    void Watch(Window owner)
    {
        if (_watched != null)
        {
            _watched.LocationChanged -= OwnerMoved;
            _watched.SizeChanged -= OwnerMoved;
            _watched.StateChanged -= OwnerMoved;
        }
        _watched = owner;
        owner.LocationChanged += OwnerMoved;
        owner.SizeChanged += OwnerMoved;
        owner.StateChanged += OwnerMoved;
    }

    void OwnerMoved(object? sender, EventArgs e)
    {
        if (IsVisible && _watched != null) FitTo(_watched);
    }

    protected override void OnClosed(EventArgs e)
    {
        IsClosed = true;
        CancelWork();
        _body.Child = null;
        if (_watched != null)
        {
            _watched.LocationChanged -= OwnerMoved;
            _watched.SizeChanged -= OwnerMoved;
            _watched.StateChanged -= OwnerMoved;
            _watched = null;
        }
        ThemeManager.Release(Resources);
        base.OnClosed(e);
    }
}
