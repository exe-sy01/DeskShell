using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ManagedShell.WindowsTasks;
using static DeskShell.WindowManagement.WmNative;

namespace DeskShell.WindowManagement;

/// <summary>
/// A full-colour window that shows live previews of other windows (DWM thumbnails).
/// DWM can't draw previews into see-through windows, so these windows are solid.
/// </summary>
internal abstract class ThumbnailWindow : Window
{
    protected sealed class Card
    {
        public required ApplicationWindow Window { get; init; }
        public required Border Root { get; init; }
        public required Border Ring { get; init; }
        public required Border Slot { get; init; }
        public IntPtr Thumb;
    }

    protected readonly List<Card> Cards = new();
    protected IntPtr Hwnd { get; private set; }

    protected ThumbnailWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = false;
        SetResourceReference(BackgroundProperty, "BgBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
        UseLayoutRounding = true;
        Left = Top = -20000;
        Width = Height = 100;
        SourceInitialized += (_, _) => Hwnd = new WindowInteropHelper(this).Handle;
        LayoutUpdated += (_, _) => UpdateThumbnails();
    }

    protected Card MakeCard(ApplicationWindow window, double width, double height, bool closeButton)
    {
        var slot = new Border { Margin = new Thickness(5), CornerRadius = new CornerRadius(4) };
        slot.SetResourceReference(Border.BackgroundProperty, "TileBrush");
        if (IsIconic(window.Handle))
        {
            var minimized = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            minimized.Children.Add(new Image { Source = window.Icon, Width = 48, Height = 48 });
            minimized.Children.Add(new TextBlock { Text = "Minimised", FontSize = 12, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
            slot.Child = minimized;
        }

        var ring = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(3), BorderBrush = Brushes.Transparent, Child = slot };

        var title = new DockPanel { Height = 30, LastChildFill = true };
        title.Children.Add(new Image { Source = window.Icon, Width = 16, Height = 16, Margin = new Thickness(4, 0, 8, 0) });
        if (closeButton)
        {
            var close = new Button
            {
                Style = (Style)Application.Current.FindResource("GhostButton"),
                Width = 26,
                Height = 26,
                ToolTip = "Close window",
                Content = new TextBlock { Text = "", Style = (Style)Application.Current.FindResource("Icon"), FontSize = 10 }
            };
            close.Click += (_, e) =>
            {
                e.Handled = true;
                window.Close();
                OnWindowClosed(window);
            };
            DockPanel.SetDock(close, Dock.Right);
            title.Children.Add(close);
        }
        title.Children.Add(new TextBlock { Text = window.Title, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(title, 1);
        grid.Children.Add(ring);
        grid.Children.Add(title);

        var root = new Border { Width = width, Height = height, Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = grid };
        var card = new Card { Window = window, Root = root, Ring = ring, Slot = slot };
        root.MouseEnter += (_, _) => OnHover(card);
        root.MouseLeftButtonUp += (_, _) => OnPick(card);
        root.MouseUp += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            window.Close();
            OnWindowClosed(window);
        };
        Cards.Add(card);
        return card;
    }

    protected virtual void OnHover(Card card) { }
    protected abstract void OnPick(Card card);
    protected virtual void OnWindowClosed(ApplicationWindow window) { }

    protected void Highlight(Card? selected)
    {
        var accent = (Brush)Application.Current.FindResource("AccentBrush");
        foreach (var c in Cards) c.Ring.BorderBrush = c == selected ? accent : Brushes.Transparent;
    }

    protected void RegisterThumbnails()
    {
        foreach (var card in Cards)
        {
            if (card.Thumb != IntPtr.Zero || IsIconic(card.Window.Handle)) continue;
            if (DwmRegisterThumbnail(Hwnd, card.Window.Handle, out var thumb) == 0) card.Thumb = thumb;
        }
        UpdateThumbnails();
    }

    protected void ClearCards()
    {
        foreach (var card in Cards)
            if (card.Thumb != IntPtr.Zero) DwmUnregisterThumbnail(card.Thumb);
        Cards.Clear();
    }

    void UpdateThumbnails()
    {
        if (Hwnd == IntPtr.Zero || Cards.Count == 0 || !IsVisible) return;
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        foreach (var card in Cards)
        {
            if (card.Thumb == IntPtr.Zero || !card.Slot.IsLoaded || card.Slot.ActualWidth < 4) continue;
            Point origin;
            try { origin = card.Slot.TransformToAncestor(this).Transform(new Point(0, 0)); }
            catch (InvalidOperationException) { continue; }

            var w = card.Slot.ActualWidth * scale;
            var h = card.Slot.ActualHeight * scale;
            var x = origin.X * scale;
            var y = origin.Y * scale;

            // Fit the window's shape inside the slot.
            if (DwmQueryThumbnailSourceSize(card.Thumb, out var src) == 0 && src.cx > 0 && src.cy > 0)
            {
                var fit = Math.Min(w / src.cx, h / src.cy);
                var fw = src.cx * fit;
                var fh = src.cy * fit;
                x += (w - fw) / 2;
                y += (h - fh) / 2;
                w = fw;
                h = fh;
            }

            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = new RECT((int)x, (int)y, (int)(x + w), (int)(y + h)),
                opacity = 255,
                fVisible = 1,
                fSourceClientAreaOnly = 0
            };
            DwmUpdateThumbnailProperties(card.Thumb, ref props);
        }
    }

    /// <summary>Covers a monitor (or a centred area of it), in physical pixels.</summary>
    protected void PlaceOn(System.Drawing.Rectangle bounds)
    {
        new WindowInteropHelper(this).EnsureHandle();
        if (!IsVisible) Show();
        for (var i = 0; i < 2; i++)
            SetWindowPos(Hwnd, HWND_TOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_SHOWWINDOW);
    }
}
