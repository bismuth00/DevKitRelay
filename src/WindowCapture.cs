using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DevKitRelay;

internal sealed record CapturedVideoFrame(byte[] Bgr, int Width, int Height);

internal sealed class WindowCapture(IntPtr windowHandle)
{
    public CapturedVideoFrame CaptureBgrFrame()
    {
        using var bitmap = CaptureBitmap();
        var area = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var bitmapData = bitmap.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

        try
        {
            var rowBytes = bitmap.Width * 3;
            var sample = new byte[rowBytes * bitmap.Height];

            for (var y = 0; y < bitmap.Height; y++)
            {
                var source = IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride);
                Marshal.Copy(source, sample, y * rowBytes, rowBytes);
            }

            return new CapturedVideoFrame(sample, bitmap.Width, bitmap.Height);
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }
    }

    private Bitmap CaptureBitmap()
    {
        if (!GetWindowRect(windowHandle, out var rect))
        {
            throw new InvalidOperationException("Failed to read window bounds.");
        }

        var width = Math.Max(2, rect.Right - rect.Left);
        var height = Math.Max(2, rect.Bottom - rect.Top);

        width -= width % 2;
        height -= height % 2;

        var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        var targetDc = graphics.GetHdc();

        try
        {
            if (!PrintWindow(windowHandle, targetDc, PrintWindowFlags.RenderFullContent))
            {
                graphics.ReleaseHdc(targetDc);
                targetDc = IntPtr.Zero;
                using var screenGraphics = Graphics.FromImage(bitmap);
                screenGraphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height));
            }
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
        finally
        {
            if (targetDc != IntPtr.Zero)
            {
                graphics.ReleaseHdc(targetDc);
            }
        }

        return bitmap;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, PrintWindowFlags flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [Flags]
    private enum PrintWindowFlags : uint
    {
        None = 0,
        RenderFullContent = 0x00000002
    }
}
