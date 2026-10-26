#if WINDOWS
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace Squelch.Client.Services.Interop;

/// <summary>Bridges Win32 (HWND, D3D11) to the WinRT Windows.Graphics.Capture types. Declarations + thin helpers only.</summary>
internal static class D3D11Interop
{
    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_SDK_VERSION = 7;

    private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IID_IGraphicsCaptureSession3 = new("F2CDD966-22AE-5EA1-9596-3A289344C3BE");

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, in Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, in Guid iid);
    }

    public static IDirect3DDevice CreateDevice()
    {
        Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D11_SDK_VERSION, out var d3d, out _, out var ctx));
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, in IID_IDXGIDevice, out var dxgi));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                try { return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
                finally { Marshal.Release(inspectable); }
            }
            finally { Marshal.Release(dxgi); }
        }
        finally
        {
            Marshal.Release(ctx);
            Marshal.Release(d3d);
        }
    }

    public static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        var factory = WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();
        var ptr = interop.CreateForWindow(hwnd, in IID_IGraphicsCaptureItem);
        try { return GraphicsCaptureItem.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }

    /// <summary>Hides the yellow capture border (Windows 11; IGraphicsCaptureSession3.IsBorderRequired isn't in
    /// the 19041 projection the client targets, so it's set through the raw interface). Best effort.</summary>
    public static unsafe void TryDisableBorder(GraphicsCaptureSession session)
    {
        var unk = WinRT.MarshalInspectable<GraphicsCaptureSession>.FromManaged(session);
        try
        {
            if (Marshal.QueryInterface(unk, in IID_IGraphicsCaptureSession3, out var s3) != 0) return;
            try
            {
                // vtable: IUnknown(0-2), IInspectable(3-5), get_IsBorderRequired(6), put_IsBorderRequired(7)
                var put = (delegate* unmanaged[Stdcall]<IntPtr, byte, int>)(*(IntPtr**)s3)[7];
                put(s3, 0);
            }
            finally { Marshal.Release(s3); }
        }
        finally { Marshal.Release(unk); }
    }
}
#endif
