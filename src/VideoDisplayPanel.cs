using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DevKitRelay;

/// <summary>
/// Renders decoded frames. Replaces a PictureBox in Zoom mode, which scales with whatever
/// interpolation GDI+ picks by default and allocates a fresh Bitmap for every frame.
/// </summary>
internal sealed class VideoDisplayPanel : Control
{
    private Bitmap? _frontBuffer;
    private Bitmap? _backBuffer;

    public VideoDisplayPanel()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint |
            ControlStyles.Opaque,
            true);
        BackColor = Color.Black;
    }

    /// <summary>
    /// Filter used when the frame does not land on the panel at its native size. Text-heavy
    /// windows usually read best with bicubic; pixel art and crisp UI can prefer nearest.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public InterpolationMode ScalingMode { get; set; } = InterpolationMode.HighQualityBicubic;

    /// <summary>
    /// Copies one decoded BGR frame into the back buffer and presents it. Must be called on the
    /// UI thread.
    /// </summary>
    public void UpdateFrame(byte[] sample, int width, int height, int sourceStride)
    {
        EnsureBuffers(width, height);

        var buffer = _backBuffer!;
        var area = new Rectangle(0, 0, width, height);
        var bitmapData = buffer.LockBits(area, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

        try
        {
            var rowBytes = width * 3;
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(
                    sample,
                    y * sourceStride,
                    IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride),
                    rowBytes);
            }
        }
        finally
        {
            buffer.UnlockBits(bitmapData);
        }

        (_frontBuffer, _backBuffer) = (buffer, _frontBuffer);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;

        if (_frontBuffer is not { } frame)
        {
            graphics.Clear(BackColor);
            return;
        }

        var destination = GetLetterboxedBounds(frame.Size, ClientSize);
        if (destination.Width <= 0 || destination.Height <= 0)
        {
            graphics.Clear(BackColor);
            return;
        }

        FillLetterbox(graphics, destination);

        // Drawing 1:1 through a resampling filter still softens the image, so copy exactly when
        // the frame already fits.
        graphics.InterpolationMode = destination.Size == frame.Size
            ? InterpolationMode.NearestNeighbor
            : ScalingMode;

        // Without this GDI+ offsets sampling by half a pixel, which smears the outer edge.
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImage(frame, destination);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Painted in OnPaint so the letterbox bars do not flicker.
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _frontBuffer?.Dispose();
            _backBuffer?.Dispose();
            _frontBuffer = null;
            _backBuffer = null;
        }

        base.Dispose(disposing);
    }

    private void FillLetterbox(Graphics graphics, Rectangle destination)
    {
        if (destination.Location == Point.Empty && destination.Size == ClientSize)
        {
            return;
        }

        using var background = new SolidBrush(BackColor);
        foreach (var bar in GetLetterboxBars(destination, ClientSize))
        {
            graphics.FillRectangle(background, bar);
        }
    }

    private static IEnumerable<Rectangle> GetLetterboxBars(Rectangle destination, Size client)
    {
        if (destination.Top > 0)
        {
            yield return new Rectangle(0, 0, client.Width, destination.Top);
        }

        if (destination.Bottom < client.Height)
        {
            yield return new Rectangle(0, destination.Bottom, client.Width, client.Height - destination.Bottom);
        }

        if (destination.Left > 0)
        {
            yield return new Rectangle(0, destination.Top, destination.Left, destination.Height);
        }

        if (destination.Right < client.Width)
        {
            yield return new Rectangle(
                destination.Right,
                destination.Top,
                client.Width - destination.Right,
                destination.Height);
        }
    }

    /// <summary>
    /// Aspect-preserving fit, matching what PictureBoxSizeMode.Zoom produced.
    /// </summary>
    private static Rectangle GetLetterboxedBounds(Size frame, Size client)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || client.Width <= 0 || client.Height <= 0)
        {
            return Rectangle.Empty;
        }

        var scale = Math.Min((double)client.Width / frame.Width, (double)client.Height / frame.Height);
        var width = Math.Max(1, (int)Math.Round(frame.Width * scale));
        var height = Math.Max(1, (int)Math.Round(frame.Height * scale));

        return new Rectangle((client.Width - width) / 2, (client.Height - height) / 2, width, height);
    }

    private void EnsureBuffers(int width, int height)
    {
        if (_frontBuffer is { } existing && existing.Width == width && existing.Height == height)
        {
            return;
        }

        _frontBuffer?.Dispose();
        _backBuffer?.Dispose();
        _frontBuffer = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        _backBuffer = new Bitmap(width, height, PixelFormat.Format24bppRgb);
    }
}
