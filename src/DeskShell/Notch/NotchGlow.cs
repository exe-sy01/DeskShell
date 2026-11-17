using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DeskShell.Notch;

/// <summary>
/// The notch's glowing edge, in a click-through window of its own on top of the notch. It's drawn
/// once; breathing only changes the whole window's transparency (UpdateLayeredWindow with no new
/// picture), so Windows fades it without WPF redrawing anything. Animating it inside the notch
/// window cost several percent of a CPU core; this costs next to nothing.
/// </summary>
sealed class NotchGlow : Window
{
    readonly Path[] _layers = new Path[3];
    static readonly byte[] Alphas = [0x14, 0x30, 0xF0];
    static readonly double[] Thicknesses = [11, 5, 1.6];
    IntPtr _hwnd;
    byte _alpha = 255;

    public NotchGlow(Color colour)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        IsHitTestVisible = false;
        Left = Top = -20000;
        Width = Height = 1;
        Title = "DeskShell Notch Glow";
        var canvas = new Canvas();
        for (var i = 0; i < 3; i++)
        {
            _layers[i] = new Path { StrokeThickness = Thicknesses[i], StrokeLineJoin = PenLineJoin.Round };
            canvas.Children.Add(_layers[i]);
        }
        Content = canvas;
        SetColour(colour);
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            // Click-through and never focused: the notch underneath gets the mouse.
            WindowManagement.WmNative.AddExStyle(_hwnd, 0x20 /* TRANSPARENT */ | 0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */);
        };
    }

    public void SetColour(Color c)
    {
        for (var i = 0; i < 3; i++) _layers[i].Stroke = UI.Frozen(Color.FromArgb(Alphas[i], c.R, c.G, c.B));
    }

    public void SetEdge(Geometry edge)
    {
        foreach (var layer in _layers) layer.Data = edge;
    }

    /// <summary>Puts the glow exactly over the notch (physical pixels) and above it.</summary>
    public void PlaceOver(int x, int y, int w, int h, double width, double height)
    {
        Width = width;
        Height = height;
        if (_hwnd == IntPtr.Zero) return;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        _alpha = 0; // WPF redraws after a resize at full strength; set ours again on the next step
    }

    /// <summary>Fades the whole glow (0–1) without redrawing it.</summary>
    public void SetStrength(double strength)
    {
        var alpha = (byte)Math.Round(Math.Clamp(strength, 0, 1) * 255);
        if (_hwnd == IntPtr.Zero || alpha == _alpha) return;
        _alpha = alpha;
        var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
        UpdateLayeredWindow(_hwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref blend, 2 /* ULW_ALPHA */);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll")]
    static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, IntPtr psize, IntPtr hdcSrc, IntPtr pptSrc, int crKey, ref BLENDFUNCTION blend, int flags);
}
