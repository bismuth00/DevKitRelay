using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using SharpGen.Runtime;

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
        var sourceSize = _lastSourceSize.IsEmpty
            ? new Size(_captureItem.Size.Width, _captureItem.Size.Height)
            : _lastSourceSize;
        var frameSize = scale < 0.999 ? ScaleSize(sourceSize, scale) : sourceSize;
        return WindowCapture.CreateMetadata(sourceSize.Width, sourceSize.Height, frameSize.Width, frameSize.Height, scale);
    }

    public CapturedVideoFrame CaptureBgrFrame(double scale, Size? outputSize = null)
    {
        var frame = TakeLatestFrame();

        using (frame)
        {
            _lastSourceSize = new Size(frame.ContentSize.Width, frame.ContentSize.Height);
            using var texture = GetTextureFromSurface(frame.Surface);
            var sourceFrame = CopyTextureToBgr(texture, frame.ContentSize.Width, frame.ContentSize.Height);

            if (scale >= 0.999 && outputSize is null)
            {
                return sourceFrame;
            }

            return ResizeFrame(sourceFrame, scale, outputSize);
        }
    }

    public void Dispose()
    {
        _session.Dispose();
        _framePool.Dispose();
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

        lock (_frameLock)
        {
            _latestFrame?.Dispose();
            _latestFrame = next;
        }

        if (next.ContentSize.Width != _lastSourceSize.Width || next.ContentSize.Height != _lastSourceSize.Height)
        {
            _lastSourceSize = new Size(next.ContentSize.Width, next.ContentSize.Height);
            sender.Recreate(
                _direct3DDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                next.ContentSize);
        }
    }

    private Direct3D11CaptureFrame TakeLatestFrame()
    {
        var deadline = Environment.TickCount64 + 500;

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
                throw new InvalidOperationException("Windows Graphics Capture frame was not available yet.");
            }

            Thread.Sleep(5);
        }
    }

    private ID3D11Texture2D GetTextureFromSurface(IDirect3DSurface surface)
    {
        var surfaceUnknown = Marshal.GetIUnknownForObject(surface);
        try
        {
            var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(surfaceUnknown);
            access.GetInterface(typeof(ID3D11Texture2D).GUID, out var texturePointer);
            return new ID3D11Texture2D(texturePointer);
        }
        finally
        {
            Marshal.Release(surfaceUnknown);
        }
    }

    private CapturedVideoFrame CopyTextureToBgr(ID3D11Texture2D texture, int sourceWidth, int sourceHeight)
    {
        var desc = texture.Description;
        desc.BindFlags = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        desc.MiscFlags = ResourceOptionFlags.None;
        desc.Usage = ResourceUsage.Staging;

        using var staging = _d3dDevice.CreateTexture2D(desc);
        _d3dContext.CopyResource(staging, texture);
        _d3dContext.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped);

        try
        {
            var bgr = new byte[sourceWidth * sourceHeight * 3];
            var sourceRow = mapped.DataPointer;

            for (var y = 0; y < sourceHeight; y++)
            {
                var sourceOffset = IntPtr.Add(sourceRow, checked((int)(y * mapped.RowPitch)));
                var destinationOffset = y * sourceWidth * 3;
                CopyBgraRowToBgr(sourceOffset, bgr, destinationOffset, sourceWidth);
            }

            return new CapturedVideoFrame(bgr, sourceWidth, sourceHeight, sourceWidth, sourceHeight);
        }
        finally
        {
            _d3dContext.Unmap(staging, 0);
        }
    }

    private static void CopyBgraRowToBgr(IntPtr source, byte[] destination, int destinationOffset, int width)
    {
        var bgra = new byte[width * 4];
        Marshal.Copy(source, bgra, 0, bgra.Length);

        for (var x = 0; x < width; x++)
        {
            destination[destinationOffset + x * 3] = bgra[x * 4];
            destination[destinationOffset + x * 3 + 1] = bgra[x * 4 + 1];
            destination[destinationOffset + x * 3 + 2] = bgra[x * 4 + 2];
        }
    }

    private static CapturedVideoFrame ResizeFrame(CapturedVideoFrame frame, double scale, Size? outputSize)
    {
        using var bitmap = new Bitmap(frame.Width, frame.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        var area = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var bitmapData = bitmap.LockBits(area, System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);

        try
        {
            var rowBytes = frame.Width * 3;
            for (var y = 0; y < frame.Height; y++)
            {
                Marshal.Copy(frame.Bgr, y * rowBytes, IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride), rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }

        var size = outputSize ?? (scale < 0.999 ? ScaleSize(bitmap.Size, scale) : bitmap.Size);
        using var resized = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
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
        var area = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var bitmapData = bitmap.LockBits(area, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);

        try
        {
            var rowBytes = bitmap.Width * 3;
            var sample = new byte[rowBytes * bitmap.Height];
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride), sample, y * rowBytes, rowBytes);
            }

            return new CapturedVideoFrame(sample, bitmap.Width, bitmap.Height, sourceWidth, sourceHeight);
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }
    }

    private static Size ScaleSize(Size sourceSize, double scale)
    {
        var width = Math.Max(2, (int)Math.Round(sourceSize.Width * scale));
        var height = Math.Max(2, (int)Math.Round(sourceSize.Height * scale));
        width -= width % 2;
        height -= height % 2;
        return new Size(width, height);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        void GetInterface([MarshalAs(UnmanagedType.LPStruct)] Guid iid, out IntPtr p);
    }
}
