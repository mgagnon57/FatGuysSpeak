#if WINDOWS
using SysDrawing = System.Drawing;
using SysImaging = System.Drawing.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FatGuysSpeak.Client.Models;

namespace FatGuysSpeak.Client.Services;

/// <summary>Share Screen: GDI capture of a picked window or the whole screen, for troubleshooting.
/// Sends no audio (Stream Game is the one with sound — see GameCaptureService).</summary>
public sealed class ScreenStreamService : IDisposable
{
    public event Action<byte[]>? FrameCaptured;
    public bool IsCapturing { get; private set; }

    private Timer? _timer;
    private int _busy;
    private int _fps = 20;
    private volatile int _jpegQuality = 75;
    private volatile int _maxWidth = 1920;
    private IntPtr _targetWindow;

    // ── P/Invoke ────────────────────────────────────────────────────────────
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int n);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder sb, int n);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc fn, IntPtr lp);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const uint PW_RENDERFULLCONTENT = 2;

    // ── Window enumeration ──────────────────────────────────────────────────
    public static List<AppWindow> GetOpenWindows()
    {
        var result = new List<AppWindow>();
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            int len = GetWindowTextLength(hWnd);
            if (len == 0) return true;
            var sb = new System.Text.StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            var title = sb.ToString().Trim();
            if (string.IsNullOrEmpty(title)) return true;
            GetWindowThreadProcessId(hWnd, out uint pid);
            string processName;
            try { processName = Process.GetProcessById((int)pid).ProcessName; }
            catch { processName = ""; }
            result.Add(new AppWindow(hWnd, title, processName));
            return true;
        }, IntPtr.Zero);
        return [.. result.OrderBy(w => w.ProcessName).ThenBy(w => w.Title)];
    }

    // ── Capture lifecycle ───────────────────────────────────────────────────
    public void StartCapture(IntPtr targetWindow = default, int fps = 20)
    {
        if (IsCapturing) return;
        _targetWindow = targetWindow;
        _fps = fps;
        IsCapturing = true;
        _busy = 0;
        _timer = new Timer(_ => CaptureAndEmit(), null, 0, 1000 / fps);
    }

    public void StopCapture()
    {
        IsCapturing = false;
        _timer?.Dispose();
        _timer = null;
        _targetWindow = IntPtr.Zero;
    }

    /// <summary>Live capture bounds in screen pixels: (left, top, width, height).
    /// Full-desktop share = the primary screen; window share = the window's current rect.</summary>
    public (int Left, int Top, int Width, int Height) CurrentCaptureRect
    {
        get
        {
            if (_targetWindow != IntPtr.Zero && GetWindowRect(_targetWindow, out var r))
                return (r.Left, r.Top, Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));
            return (0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
        }
    }

    public void UpdateQuality(int fps, int jpegQuality, int maxWidth)
    {
        _jpegQuality = jpegQuality;
        _maxWidth = maxWidth;
        if (fps != _fps)
        {
            _fps = fps;
            if (IsCapturing)
                _timer?.Change(0, 1000 / fps);
        }
    }

    private void CaptureAndEmit()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try { FrameCaptured?.Invoke(CaptureFrameAsJpeg()); }
        catch { }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private byte[] CaptureFrameAsJpeg()
    {
        SysDrawing.Bitmap raw;

        if (_targetWindow != IntPtr.Zero)
        {
            GetWindowRect(_targetWindow, out var rect);
            int w = Math.Max(1, rect.Right - rect.Left);
            int h = Math.Max(1, rect.Bottom - rect.Top);
            raw = new SysDrawing.Bitmap(w, h, SysImaging.PixelFormat.Format32bppArgb);
            using (var g = SysDrawing.Graphics.FromImage(raw))
            {
                var hdc = g.GetHdc();
                PrintWindow(_targetWindow, hdc, PW_RENDERFULLCONTENT);
                g.ReleaseHdc(hdc);
            }
        }
        else
        {
            int sw = GetSystemMetrics(SM_CXSCREEN);
            int sh = GetSystemMetrics(SM_CYSCREEN);
            raw = new SysDrawing.Bitmap(sw, sh, SysImaging.PixelFormat.Format32bppArgb);
            using (var g = SysDrawing.Graphics.FromImage(raw))
                g.CopyFromScreen(0, 0, 0, 0, new SysDrawing.Size(sw, sh), SysDrawing.CopyPixelOperation.SourceCopy);
        }

        return JpegFrameEncoder.Encode(raw, _maxWidth, _jpegQuality);
    }

    public void Dispose() => StopCapture();
}
#else
using FatGuysSpeak.Client.Models;

namespace FatGuysSpeak.Client.Services;

public sealed class ScreenStreamService : IDisposable
{
    public event Action<byte[]>? FrameCaptured;
    public bool IsCapturing { get; private set; }
    public static List<AppWindow> GetOpenWindows() => [];
    public void StartCapture(IntPtr targetWindow = default, int fps = 20) { }
    public void StopCapture() { }
    public void UpdateQuality(int fps, int jpegQuality, int maxWidth) { }
    public (int Left, int Top, int Width, int Height) CurrentCaptureRect => (0, 0, 1, 1);
    public void Dispose() { }
}
#endif
