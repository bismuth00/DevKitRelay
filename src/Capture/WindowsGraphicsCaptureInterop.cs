using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace DevKitRelay;

internal static class WindowsGraphicsCaptureInterop
{
    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid GraphicsCaptureItemInteropGuid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    public static GraphicsCaptureItem CreateItemForWindow(IntPtr windowHandle)
    {
        var className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        var hstring = IntPtr.Zero;
        var factory = IntPtr.Zero;
        var item = IntPtr.Zero;

        try
        {
            ThrowIfFailed(WindowsCreateString(className, className.Length, out hstring));
            ThrowIfFailed(RoGetActivationFactory(hstring, GraphicsCaptureItemInteropGuid, out factory));
            var interop = Marshal.GetObjectForIUnknown(factory) as IGraphicsCaptureItemInterop
                ?? throw new InvalidOperationException("GraphicsCaptureItem interop factory is not available.");
            ThrowIfFailed(interop.CreateForWindow(windowHandle, GraphicsCaptureItemGuid, out item));
            return (GraphicsCaptureItem)Marshal.GetObjectForIUnknown(item);
        }
        finally
        {
            if (item != IntPtr.Zero)
            {
                Marshal.Release(item);
            }

            if (factory != IntPtr.Zero)
            {
                Marshal.Release(factory);
            }

            if (hstring != IntPtr.Zero)
            {
                WindowsDeleteString(hstring);
            }
        }
    }

    public static IDirect3DDevice CreateDirect3DDevice(IntPtr dxgiDevice)
    {
        ThrowIfFailed(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectable));
        try
        {
            return (IDirect3DDevice)Marshal.GetObjectForIUnknown(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    private static void ThrowIfFailed(int hresult)
    {
        if (hresult < 0)
        {
            Marshal.ThrowExceptionForHR(hresult);
        }
    }

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId,
        [MarshalAs(UnmanagedType.LPStruct)] Guid iid,
        out IntPtr factory);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice,
        out IntPtr graphicsDevice);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(IntPtr window, [MarshalAs(UnmanagedType.LPStruct)] Guid iid, out IntPtr result);

        [PreserveSig]
        int CreateForMonitor(IntPtr monitor, [MarshalAs(UnmanagedType.LPStruct)] Guid iid, out IntPtr result);
    }
}
