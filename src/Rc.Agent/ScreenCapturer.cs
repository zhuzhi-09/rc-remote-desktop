using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Rc.Agent;

/// <summary>
/// Captures the primary monitor into a reusable <see cref="PixelFormat.Format32bppRgb"/> bitmap.
/// A GDI <c>BitBlt</c> copies from the screen device context into the bitmap's memory DC, which is
/// considerably faster than a managed <c>CopyFromScreen</c> per frame.
/// </summary>
public sealed class ScreenCapturer : IDisposable
{
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SRCCOPY = 0x00CC0020;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        IntPtr hdcDest, int xDest, int yDest, int width, int height,
        IntPtr hdcSrc, int xSrc, int ySrc, int rop);

    private Bitmap? _bitmap;

    public int ScreenWidth => GetSystemMetrics(SM_CXSCREEN);

    public int ScreenHeight => GetSystemMetrics(SM_CYSCREEN);

    /// <summary>Captures the primary monitor. The returned bitmap is owned and reused by this instance.</summary>
    public Bitmap Capture()
    {
        var width = ScreenWidth;
        var height = ScreenHeight;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException($"Primary monitor reported an invalid size {width}x{height}.");
        }

        if (_bitmap is null || _bitmap.Width != width || _bitmap.Height != height)
        {
            _bitmap?.Dispose();
            _bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        }

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetDC(NULL) failed.");
        }

        using var graphics = Graphics.FromImage(_bitmap);
        var memoryDc = graphics.GetHdc();
        try
        {
            if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, 0, 0, SRCCOPY))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "BitBlt failed.");
            }
        }
        finally
        {
            graphics.ReleaseHdc(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        return _bitmap;
    }

    public void Dispose() => _bitmap?.Dispose();
}
