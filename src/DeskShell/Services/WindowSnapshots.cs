using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeskShell.Services;

/// <summary>
/// Small still pictures of windows for the shell's window board. The shell is a see-through
/// window, which Windows can't draw live previews into, so we ask each window to paint itself
/// (PrintWindow) and keep a scaled-down copy.
/// </summary>
internal static class WindowSnapshots
{
    const uint PW_RENDERFULLCONTENT = 0x2;
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    /// <summary>A snapshot at most <paramref name="maxWidth"/> pixels wide, or null for minimised/hung windows.</summary>
    public static BitmapSource? Capture(IntPtr hwnd, int maxWidth)
    {
        if (IsIconic(hwnd) || IsHungAppWindow(hwnd) || !GetWindowRect(hwnd, out var outer)) return null;
        var width = outer.Right - outer.Left;
        var height = outer.Bottom - outer.Top;
        if (width < 20 || height < 20) return null;

        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        var old = SelectObject(memDc, bitmap);
        try
        {
            if (!PrintWindow(hwnd, memDc, PW_RENDERFULLCONTENT)) return null;
            SelectObject(memDc, old);

            BitmapSource full = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            // Trim the invisible resize border around modern windows.
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var frame, Marshal.SizeOf<RECT>()) == 0)
            {
                var crop = new Int32Rect(
                    Math.Max(0, frame.Left - outer.Left), Math.Max(0, frame.Top - outer.Top),
                    Math.Min(width, frame.Right - frame.Left), Math.Min(height, frame.Bottom - frame.Top));
                if (crop.Width > 0 && crop.Height > 0 && crop.X + crop.Width <= width && crop.Y + crop.Height <= height)
                    full = new CroppedBitmap(full, crop);
            }

            // Scale down and copy into a small standalone bitmap so the full-size one can be freed.
            var scale = Math.Min(1.0, (double)maxWidth / full.PixelWidth);
            BitmapSource scaled = new TransformedBitmap(full, new ScaleTransform(scale, scale));
            var stride = scaled.PixelWidth * 4;
            var pixels = new byte[stride * scaled.PixelHeight];
            var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgr32, null, 0);
            converted.CopyPixels(pixels, stride, 0);
            var small = BitmapSource.Create(scaled.PixelWidth, scaled.PixelHeight, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
            small.Freeze();
            return small;
        }
        catch
        {
            return null;
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(bitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsHungAppWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
}
