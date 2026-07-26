using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using SharpGen.Runtime;
using SIPSorceryMedia.Abstractions;
using WinRT;

namespace DevKitRelay;

internal sealed class WindowsGraphicsWindowCapture : IWindowCapture
{
    private readonly IntPtr _windowHandle;
    private readonly GraphicsCaptureItem _captureItem;
    private readonly ID3D11Device _d3dDevice;
    private readonly ID3D11DeviceContext _d3dContext;
    private readonly IDirect3DDevice _direct3DDevice;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private readonly object _frameLock = new();
    private Direct3D11CaptureFrame? _latestFrame;
    private Size _lastSourceSize = Size.Empty;

    // A window that is not redrawing produces no capture frames at all, so the most recent one is
    // kept and re-sent to hold the stream open instead of failing every send-loop iteration.
    private CapturedVideoFrame? _lastConvertedFrame;

    // Reused across frames; recreating a staging texture per frame is a significant cost at high
    // resolutions.
    private ID3D11Texture2D? _stagingTexture;
    private Size _stagingSize = Size.Empty;

    public WindowsGraphicsWindowCapture(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        _captureItem = WindowsGraphicsCaptureInterop.CreateItemForWindow(windowHandle);
        _lastSourceSize = new Size(_captureItem.Size.Width, _captureItem.Size.Height);

        var createdDevice = D3D11.D3D11CreateDevice(
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0);
        _d3dDevice = createdDevice;
        _d3dContext = _d3dDevice.ImmediateContext;

        using var dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>();
        _direct3DDevice = WindowsGraphicsCaptureInterop.CreateDirect3DDevice(dxgiDevice.NativePointer);
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _direct3DDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            new SizeInt32(_captureItem.Size.Width, _captureItem.Size.Height));
        _framePool.FrameArrived += OnFrameArrived;
        _session = _framePool.CreateCaptureSession(_captureItem);
        _session.StartCapture();
    }

    public static bool IsSupported => GraphicsCaptureSession.IsSupported();

    public VideoMetadata GetVideoMetadata(double scale)
    {
        Size sourceSize;
        lock (_frameLock)
        {
            sourceSize = _lastSourceSize;
        }

        if (sourceSize.IsEmpty)
        {
            sourceSize = new Size(_captureItem.Size.Width, _captureItem.Size.Height);
        }

        sourceSize = FrameGeometry.ToEncodableSize(sourceSize);
        var frameSize = FrameGeometry.ScaleSize(sourceSize, scale);
        return WindowCapture.CreateMetadata(sourceSize.Width, sourceSize.Height, frameSize.Width, frameSize.Height, scale);
    }

    public CapturedVideoFrame CaptureBgrFrame(double scale, Size? outputSize = null)
    {
        var frame = TakeLatestFrame();
        if (frame is null)
        {
            return _lastConvertedFrame
                ?? throw new InvalidOperationException("Windows Graphics Capture has not produced a frame yet.");
        }

        using (frame)
        {
            var contentSize = new Size(frame.ContentSize.Width, frame.ContentSize.Height);
            lock (_frameLock)
            {
                _lastSourceSize = contentSize;
            }

            using var texture = GetTextureFromSurface(frame.Surface);
            var sourceFrame = CopyTextureToBgra(texture, contentSize.Width, contentSize.Height);

            // The capture item reports the raw window size, which is frequently odd. The encoder
            // is fixed to the first frame's size, so honour outputSize once it is established.
            var targetSize = outputSize ?? FrameGeometry.ScaleSize(contentSize, scale);
            var converted = targetSize.Width == sourceFrame.Width && targetSize.Height == sourceFrame.Height
                ? sourceFrame
                : ResizeFrame(sourceFrame, targetSize);

            _lastConvertedFrame = converted;
            return converted;
        }
    }

    public void Dispose()
    {
        _session.Dispose();
        _framePool.Dispose();
        _stagingTexture?.Dispose();
        _direct3DDevice.Dispose();
        _d3dContext.Dispose();
        _d3dDevice.Dispose();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        var next = sender.TryGetNextFrame();
        if (next is null)
        {
            return;
        }

        bool sizeChanged;
        lock (_frameLock)
        {
            _latestFrame?.Dispose();
            _latestFrame = next;

            sizeChanged = next.ContentSize.Width != _lastSourceSize.Width ||
                next.ContentSize.Height != _lastSourceSize.Height;
            if (sizeChanged)
            {
                _lastSourceSize = new Size(next.ContentSize.Width, next.ContentSize.Height);
            }
        }

        if (sizeChanged)
        {
            sender.Recreate(
                _direct3DDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                next.ContentSize);
        }
    }

    /// <summary>
    /// Returns the newest capture frame, or null when the window has not redrawn. Waits only long
    /// enough to catch a frame that is about to arrive; the send loop already paces the caller.
    /// </summary>
    private Direct3D11CaptureFrame? TakeLatestFrame()
    {
        var deadline = Environment.TickCount64 + 8;

        while (true)
        {
            lock (_frameLock)
            {
                if (_latestFrame is { } frame)
                {
                    _latestFrame = null;
                    return frame;
                }
            }

            if (Environment.TickCount64 >= deadline)
            {
                return null;
            }

            Thread.Sleep(1);
        }
    }

    private ID3D11Texture2D GetTextureFromSurface(IDirect3DSurface surface)
    {
        // The surface is a CsWinRT projected object, so it has to be cast through WinRT's own
        // marshalling. Marshal.GetObjectForIUnknown produces an IInspectable wrapper that cannot
        // be cast to a plain COM interface.
        // Called as a static method because SharpGen.Runtime also defines an As<T> extension.
        var access = CastExtensions.As<IDirect3DDxgiInterfaceAccess>(surface);
        access.GetInterface(typeof(ID3D11Texture2D).GUID, out var texturePointer);
        return new ID3D11Texture2D(texturePointer);
    }

    /// <summary>
    /// Reads the captured texture back as BGRA. The alpha channel is deliberately kept: the
    /// encoder converts straight from BGRA to I420, so dropping it here would only add a second
    /// full-frame pass over every pixel.
    /// </summary>
    private CapturedVideoFrame CopyTextureToBgra(ID3D11Texture2D texture, int sourceWidth, int sourceHeight)
    {
        var staging = GetStagingTexture(texture, sourceWidth, sourceHeight);
        _d3dContext.CopyResource(staging, texture);
        _d3dContext.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped);

        try
        {
            var rowBytes = sourceWidth * 4;
            var rowPitch = checked((int)mapped.RowPitch);
            var bgra = new byte[rowBytes * sourceHeight];

            for (var y = 0; y < sourceHeight; y++)
            {
                Marshal.Copy(IntPtr.Add(mapped.DataPointer, y * rowPitch), bgra, y * rowBytes, rowBytes);
            }

            return new CapturedVideoFrame(
                bgra,
                VideoPixelFormatsEnum.Bgra,
                sourceWidth,
                sourceHeight,
                sourceWidth,
                sourceHeight);
        }
        finally
        {
            _d3dContext.Unmap(staging, 0);
        }
    }

    private ID3D11Texture2D GetStagingTexture(ID3D11Texture2D texture, int width, int height)
    {
        var size = new Size(width, height);
        if (_stagingTexture is { } existing && _stagingSize == size)
        {
            return existing;
        }

        _stagingTexture?.Dispose();

        var desc = texture.Description;
        desc.BindFlags = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        desc.MiscFlags = ResourceOptionFlags.None;
        desc.Usage = ResourceUsage.Staging;

        _stagingTexture = _d3dDevice.CreateTexture2D(desc);
        _stagingSize = size;
        return _stagingTexture;
    }

    private static CapturedVideoFrame ResizeFrame(CapturedVideoFrame frame, Size size)
    {
        const System.Drawing.Imaging.PixelFormat Format = System.Drawing.Imaging.PixelFormat.Format32bppRgb;

        using var bitmap = new Bitmap(frame.Width, frame.Height, Format);
        var area = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var bitmapData = bitmap.LockBits(area, System.Drawing.Imaging.ImageLockMode.WriteOnly, Format);

        try
        {
            var rowBytes = frame.Width * 4;
            for (var y = 0; y < frame.Height; y++)
            {
                Marshal.Copy(frame.Pixels, y * rowBytes, IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride), rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }

        using var resized = new Bitmap(size.Width, size.Height, Format);
        using (var graphics = Graphics.FromImage(resized))
        {
            graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.DrawImage(bitmap, 0, 0, size.Width, size.Height);
        }

        return CopyBitmap(resized, frame.SourceWidth, frame.SourceHeight);
    }

    private static CapturedVideoFrame CopyBitmap(Bitmap bitmap, int sourceWidth, int sourceHeight)
    {
        const System.Drawing.Imaging.PixelFormat Format = System.Drawing.Imaging.PixelFormat.Format32bppRgb;

        var area = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var bitmapData = bitmap.LockBits(area, System.Drawing.Imaging.ImageLockMode.ReadOnly, Format);

        try
        {
            var rowBytes = bitmap.Width * 4;
            var sample = new byte[rowBytes * bitmap.Height];
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride), sample, y * rowBytes, rowBytes);
            }

            return new CapturedVideoFrame(
                sample,
                VideoPixelFormatsEnum.Bgra,
                bitmap.Width,
                bitmap.Height,
                sourceWidth,
                sourceHeight);
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        void GetInterface([MarshalAs(UnmanagedType.LPStruct)] Guid iid, out IntPtr p);
    }
}
