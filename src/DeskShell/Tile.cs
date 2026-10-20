using System.Windows;

namespace DeskShell;

/// <summary>Attached second line of text for quick-setting tiles.</summary>
public static class Tile
{
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.RegisterAttached(
        "Subtitle", typeof(string), typeof(Tile), new FrameworkPropertyMetadata(""));

    public static string GetSubtitle(DependencyObject o) => (string)o.GetValue(SubtitleProperty);
    public static void SetSubtitle(DependencyObject o, string value) => o.SetValue(SubtitleProperty, value);
}
