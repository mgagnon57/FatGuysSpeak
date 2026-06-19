#if WINDOWS
using System.Runtime.InteropServices;

namespace FatGuysSpeak.Client.Services;

/// <summary>A topmost, click-through red frame drawn around the region currently being shared
/// (the Teams-style "you are sharing this" outline). Follows the target window/screen rect live.</summary>
public sealed class ShareBorderOverlay : IDisposable
{
    private const string ClassName = "FgsShareBorder";
    private const int Thickness = 4;                       // border width in px
    private const uint RedColor = 0x001E14E6;              // COLORREF 0x00BBGGRR -> R230 G20 B30

    private IntPtr _hwnd;
    private System.Threading.Timer? _follow;
    private Func<(int Left, int Top, int Width, int Height)>? _rectProvider;
    private (int, int, int, int) _last;
    private static WndProcDelegate? _wndProc;             // kept alive to avoid GC of the callback
    private static bool _classRegistered;

    public void Show(Func<(int Left, int Top, int Width, int Height)> rectProvider)
    {
        _rectProvider = rectProvider;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            EnsureWindow();
            Reposition();
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        });
        _follow?.Dispose();
        _follow = new System.Threading.Timer(_ => MainThread.BeginInvokeOnMainThread(Reposition), null, 250, 250);
    }

    public void Hide()
    {
        _follow?.Dispose();
        _follow = null;
        if (_hwnd != IntPtr.Zero)
            MainThread.BeginInvokeOnMainThread(() => ShowWindow(_hwnd, SW_HIDE));
    }

    private void EnsureWindow()
    {
        if (_hwnd != IntPtr.Zero) return;

        if (!_classRegistered)
        {
            _wndProc = static (h, m, w, l) => DefWindowProcW(h, m, w, l);
            var wc = new WNDCLASS
            {
                lpfnWndProc = _wndProc,
                hInstance = GetModuleHandleW(null),
                hbrBackground = CreateSolidBrush(RedColor),
                lpszClassName = ClassName,
            };
            RegisterClassW(ref wc);
            _classRegistered = true;
        }

        const uint ex = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
        _hwnd = CreateWindowExW(ex, ClassName, "FgsShareBorder", WS_POPUP,
            0, 0, 100, 100, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        SetLayeredWindowAttributes(_hwnd, 0, 255, LWA_ALPHA);
    }

    // Position the frame over the live capture rect and clip the window to just its border, so the
    // hollow centre shows the real screen and stays click-through.
    private void Reposition()
    {
        if (_hwnd == IntPtr.Zero || _rectProvider is null) return;
        var (left, top, width, height) = _rectProvider();
        if (width < 2 * Thickness || height < 2 * Thickness) return;

        if (!_last.Equals((left, top, width, height)))
        {
            _last = (left, top, width, height);
            var outer = CreateRectRgn(0, 0, width, height);
            var inner = CreateRectRgn(Thickness, Thickness, width - Thickness, height - Thickness);
            CombineRgn(outer, outer, inner, RGN_DIFF);
            DeleteObject(inner);
            SetWindowRgn(_hwnd, outer, true);   // window now owns 'outer'
        }
        SetWindowPos(_hwnd, HWND_TOPMOST, left, top, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    public void Dispose()
    {
        Hide();
        if (_hwnd != IntPtr.Zero) { var h = _hwnd; _hwnd = IntPtr.Zero; MainThread.BeginInvokeOnMainThread(() => DestroyWindow(h)); }
    }

    // ── Win32 ───────────────────────────────────────────────────────────────
    private const uint WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80,
                       WS_EX_TOPMOST = 0x8, WS_EX_NOACTIVATE = 0x8000000;
    private const uint WS_POPUP = 0x80000000;
    private const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    private const uint LWA_ALPHA = 0x2;
    private const int RGN_DIFF = 4, SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName, lpszClassName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassW(ref WNDCLASS c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr dst, IntPtr a, IntPtr b, int mode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
}
#else
namespace FatGuysSpeak.Client.Services;

public sealed class ShareBorderOverlay : IDisposable
{
    public void Show(Func<(int Left, int Top, int Width, int Height)> rectProvider) { }
    public void Hide() { }
    public void Dispose() { }
}
#endif
