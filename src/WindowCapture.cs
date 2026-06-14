using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace DevKitRelay;

internal sealed record CapturedVideoFrame(
    byte[] Bgr,
    int Width,
    int Height,
    int SourceWidth,
    int SourceHeight);

internal sealed class WindowCapture(IntPtr windowHandle) : IWindowCapture
{
    public void Dispose()
    {
    }

    public VideoMetadata GetVideoMetadata(double scale)
    {
        var sourceSize = GetSourceSize();
        var frameSize = scale < 0.999 ? ScaleSize(sourceSize, scale) : sourceSize;
        return CreateMetadata(sourceSize.Width, sourceSize.Height, frameSize.Width, frameSize.Height, scale);
    }

    public CapturedVideoFrame CaptureBgrFrame(double scale, Size? outputSize = null)
    {
        using var bitmap = CaptureBitmap();
        using var frameBitmap = scale < 0.999 ? ScaleBitmap(bitmap, scale) : null;
        using var outputBitmap = outputSize is { } size && size != (frameBitmap ?? bitmap).Size
            ? ResizeBitmap(frameBitmap ?? bitmap, size)
            : null;
        var source = outputBitmap ?? frameBitmap ?? bitmap;
        return CopyBgr(source, bitmap.Width, bitmap.Height);
    }

    private static CapturedVideoFrame CopyBgr(Bitmap bitmap, int sourceWidth, int sourceHeight)
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

            return new CapturedVideoFrame(sample, bitmap.Width, bitmap.Height, sourceWidth, sourceHeight);
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }
    }

    private static Bitmap ScaleBitmap(Bitmap source, double scale)
    {
        var scaledSize = ScaleSize(source.Size, scale);
        return ResizeBitmap(source, scaledSize);
    }

    private static Bitmap ResizeBitmap(Bitmap source, Size size)
    {
        var scaled = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(scaled);
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, 0, 0, size.Width, size.Height);
        return scaled;
    }

    private static Size ScaleSize(Size sourceSize, double scale)
    {
        var width = Math.Max(2, (int)Math.Round(sourceSize.Width * scale));
        var height = Math.Max(2, (int)Math.Round(sourceSize.Height * scale));

        width -= width % 2;
        height -= height % 2;
        return new Size(width, height);
    }

    public static VideoMetadata CreateMetadata(
        int sourceWidth,
        int sourceHeight,
        int frameWidth,
        int frameHeight,
        double scale)
    {
        var restoredWidth = scale < 0.999 ? (int)Math.Round(frameWidth / scale) : frameWidth;
        var restoredHeight = scale < 0.999 ? (int)Math.Round(frameHeight / scale) : frameHeight;

        restoredWidth -= restoredWidth % 2;
        restoredHeight -= restoredHeight % 2;

        return new VideoMetadata(
            sourceWidth,
            sourceHeight,
            frameWidth,
            frameHeight,
            Math.Max(sourceWidth, restoredWidth),
            Math.Max(sourceHeight, restoredHeight),
            scale);
    }

    private Size GetSourceSize()
    {
        var rect = GetWindowRectBounds();
        var width = Math.Max(2, rect.Right - rect.Left);
        var height = Math.Max(2, rect.Bottom - rect.Top);

        width -= width % 2;
        height -= height % 2;
        return new Size(width, height);
    }

    private Bitmap CaptureBitmap()
    {
        var rect = GetWindowRectBounds();
        var size = GetSourceSize();
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);

        try
        {
            if (TryPrintWindow(bitmap))
            {
                return bitmap;
            }

            CaptureVisibleScreen(bitmap);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private bool TryPrintWindow(Bitmap bitmap)
    {
        using var graphics = Graphics.FromImage(bitmap);
        var targetDc = graphics.GetHdc();

        try
        {
            return PrintWindow(windowHandle, targetDc, PrintWindowFlags.RenderFullContent);
        }
        finally
        {
            graphics.ReleaseHdc(targetDc);
        }
    }

    private void CaptureVisibleScreen(Bitmap bitmap)
    {
        var rect = GetVisibleWindowBounds();
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Black);
        graphics.CopyFromScreen(
            rect.Left,
            rect.Top,
            0,
            0,
            new Size(Math.Min(bitmap.Width, rect.Right - rect.Left), Math.Min(bitmap.Height, rect.Bottom - rect.Top)),
            CopyPixelOperation.SourceCopy);
    }

    private Rect GetVisibleWindowBounds()
    {
        if (DwmGetWindowAttribute(
                windowHandle,
                DwmWindowAttribute.ExtendedFrameBounds,
                out var extendedFrameBounds,
                Marshal.SizeOf<Rect>()) == 0)
        {
            return extendedFrameBounds;
        }

        return GetWindowRectBounds();
    }

    private Rect GetWindowRectBounds()
    {
        if (!GetWindowRect(windowHandle, out var rect))
        {
            throw new InvalidOperationException("Failed to read window bounds.");
        }

        return rect;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out Rect rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr hwnd,
        DwmWindowAttribute attribute,
        out Rect rect,
        int attributeSize);

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

    private enum DwmWindowAttribute
    {
        ExtendedFrameBounds = 9
    }
}
