using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DevKitRelay;

internal sealed class WindowCapture(IntPtr windowHandle, long jpegQuality)
{
    private readonly ImageCodecInfo _jpegCodec = ImageCodecInfo.GetImageEncoders()
        .First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);

    public byte[] CaptureJpeg()
    {
        if (!GetWindowRect(windowHandle, out var rect))
        {
            throw new InvalidOperationException("Failed to read window bounds.");
        }

        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);

        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
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
        finally
        {
            if (targetDc != IntPtr.Zero)
            {
                graphics.ReleaseHdc(targetDc);
            }
        }

        using var stream = new MemoryStream();
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, jpegQuality);
        bitmap.Save(stream, _jpegCodec, parameters);
        return stream.ToArray();
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
