using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace DevKitRelay;

internal sealed record CapturedVideoFrame(byte[] Bgr, int Width, int Height);

internal sealed class WindowCapture(IntPtr windowHandle)
{
    public CapturedVideoFrame CaptureBgrFrame(double scale)
    {
        using var bitmap = CaptureBitmap();
        using var frameBitmap = scale < 0.999 ? ScaleBitmap(bitmap, scale) : null;
        var source = frameBitmap ?? bitmap;
        return CopyBgr(source);
    }

    private static CapturedVideoFrame CopyBgr(Bitmap bitmap)
    {
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

    private static Bitmap ScaleBitmap(Bitmap source, double scale)
    {
        var width = Math.Max(2, (int)Math.Round(source.Width * scale));
        var height = Math.Max(2, (int)Math.Round(source.Height * scale));

        width -= width % 2;
        height -= height % 2;

        var scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(scaled);
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, 0, 0, width, height);
        return scaled;
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
