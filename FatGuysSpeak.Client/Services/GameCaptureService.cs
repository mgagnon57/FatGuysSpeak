#if WINDOWS
using System.Runtime.InteropServices;
using FatGuysSpeak.Client.Services.Interop;
using FatGuysSpeak.Shared;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using SysDrawing = System.Drawing;
using SysImaging = System.Drawing.Imaging;

namespace FatGuysSpeak.Client.Services;

/// <summary>Stream Game: captures one game window with Windows.Graphics.Capture (works for GPU-rendered and
/// borderless games, and never shows other windows when you alt-tab) plus that game's own sound only.</summary>
public sealed class GameCaptureService : IDisposable
{
    public event Action<byte[]>? FrameCaptured;
    public event Action<byte[]>? StreamAudioCaptured;
    public event Action? TargetClosed;
    /// <summary>Raised when the picked window's sound can't be streamed safely (it could carry voice chat).</summary>
    public event Action? GameSoundBlocked;
    public bool IsCapturing { get; private set; }

    private readonly object _gate = new();
    private IDirect3DDevice? _device;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _poolSize;
    private int _busy;
    private volatile int _fps = 20;
    private volatile int _jpegQuality = 75;
    private volatile int _maxWidth = 1920;
    private TimeSpan _lastEmit;   // zero = nothing emitted yet (frame times are time-since-boot, so the first frame always passes)

    private int _processId;
    private bool _gameSound;
    private AudioClient? _audioClient;
    private Thread? _audioThread;
    private volatile bool _audioRunning;
    private StreamAudioEncoder? _audioEncoder;

    private static readonly WaveFormat CaptureFormat = new(StreamAudioEncoder.SampleRate, 16, 2);

    public void Start(IntPtr hwnd, int processId, bool gameSound, int fps = 20)
    {
        lock (_gate)
        {
            if (IsCapturing) return;
            _fps = fps;
            _device ??= D3D11Interop.CreateDevice();
            StartVideo(hwnd);
            IsCapturing = true;
            _processId = processId;
            _gameSound = gameSound;
            if (gameSound) StartAudio();
        }
    }

    public void SwitchTarget(IntPtr hwnd, int processId)
    {
        lock (_gate)
        {
            if (!IsCapturing) return;
            // Build the new capture first: if the window can't be captured, the current stream carries on untouched.
            var (item, pool, session) = CreateVideo(hwnd);
            StopVideo();
            UseVideo(item, pool, session);
            if (processId != _processId)
            {
                StopAudio();
                _processId = processId;
                if (_gameSound) StartAudio();
            }
        }
    }

    public void SetGameSound(bool on)
    {
        lock (_gate)
        {
            _gameSound = on;
            if (!IsCapturing) return;
            if (on && _audioClient is null) StartAudio();
            else if (!on) StopAudio();
        }
    }

    public void UpdateQuality(int fps, int jpegQuality, int maxWidth)
    {
        _fps = fps;
        _jpegQuality = jpegQuality;
        _maxWidth = maxWidth;
    }

    public void Stop()
    {
        lock (_gate)
        {
            IsCapturing = false;
            StopVideo();
            StopAudio();
        }
    }

    // ── Video ───────────────────────────────────────────────────────────────
    private void StartVideo(IntPtr hwnd)
    {
        var (item, pool, session) = CreateVideo(hwnd);
        UseVideo(item, pool, session);
    }

    private (GraphicsCaptureItem, Direct3D11CaptureFramePool, GraphicsCaptureSession) CreateVideo(IntPtr hwnd)
    {
        var item = D3D11Interop.CreateItemForWindow(hwnd);
        var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        try
        {
            var session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            try { D3D11Interop.TryDisableBorder(session); } catch { /* border stays — cosmetic */ }
            return (item, pool, session);
        }
        catch { pool.Dispose(); throw; }
    }

    private void UseVideo(GraphicsCaptureItem item, Direct3D11CaptureFramePool pool, GraphicsCaptureSession session)
    {
        _item = item;
        _pool = pool;
        _session = session;
        _poolSize = item.Size;
        _item.Closed += OnItemClosed;
        _pool.FrameArrived += OnFrameArrived;
        _lastEmit = TimeSpan.Zero;
        _session.StartCapture();
    }

    private void StopVideo()
    {
        if (_item is not null) _item.Closed -= OnItemClosed;
        if (_pool is not null) _pool.FrameArrived -= OnFrameArrived;
        _session?.Dispose();
        _pool?.Dispose();
        _session = null;
        _pool = null;
        _item = null;
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        Stop();
        TargetClosed?.Invoke();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null) return;

        // The game changed resolution or switched borderless ↔ windowed: resize the pool and keep going.
        if (frame.ContentSize.Width != _poolSize.Width || frame.ContentSize.Height != _poolSize.Height)
        {
            _poolSize = frame.ContentSize;
            if (_poolSize.Width > 0 && _poolSize.Height > 0)
                sender.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
            return;
        }

        var minGap = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, _fps));
        if (frame.SystemRelativeTime - _lastEmit < minGap) return;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        _lastEmit = frame.SystemRelativeTime;
        try { FrameCaptured?.Invoke(ToJpeg(frame)); }
        catch { /* one bad frame shouldn't end the stream */ }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private byte[] ToJpeg(Direct3D11CaptureFrame frame)
    {
        using var soft = SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Ignore)
            .AsTask().GetAwaiter().GetResult();
        int w = soft.PixelWidth, h = soft.PixelHeight;
        var bytes = new byte[w * h * 4];
        soft.CopyToBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));

        var bmp = new SysDrawing.Bitmap(w, h, SysImaging.PixelFormat.Format32bppRgb);
        var data = bmp.LockBits(new SysDrawing.Rectangle(0, 0, w, h), SysImaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
        try
        {
            for (int y = 0; y < h; y++)
                Marshal.Copy(bytes, y * w * 4, data.Scan0 + y * data.Stride, w * 4);
        }
        finally { bmp.UnlockBits(data); }
        return JpegFrameEncoder.Encode(bmp, _maxWidth, _jpegQuality);
    }

    // ── Game-only audio ─────────────────────────────────────────────────────
    private void StartAudio()
    {
        // Never capture a process tree that could contain voice chat (FatGuysSpeak itself, whatever launched it,
        // voice apps). Legal requirement — the stream goes video-only instead.
        var tree = ProcessTree.Snapshot();
        var name = tree.TryGetValue(_processId, out var me) ? me.Name : "";
        if (!GameAudioPolicy.AllowsAudio(_processId, name, Environment.ProcessId,
                pid => tree.TryGetValue(pid, out var e) ? e.ParentId : null))
        {
            GameSoundBlocked?.Invoke();
            return;
        }

        AudioClient? client = null;
        try
        {
            client = ProcessLoopbackInterop.ActivateForProcessAsync(_processId).GetAwaiter().GetResult();
            client.Initialize(AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
                2_000_000 /* 200 ms */, 0, CaptureFormat, Guid.Empty);
        }
        catch (Exception ex)
        {
            client?.Dispose();
            // Video keeps streaming without sound.
            System.Diagnostics.Debug.WriteLine($"[GameCapture] game audio unavailable: {ex.Message}");
            return;
        }

        var buffer = new BufferedWaveProvider(CaptureFormat) { DiscardOnBufferOverflow = true };
        _audioClient = client;
        _audioEncoder = new StreamAudioEncoder(new StereoToMonoSampleProvider(buffer.ToSampleProvider()),
            packet => StreamAudioCaptured?.Invoke(packet));
        _audioRunning = true;
        _audioThread = new Thread(() => AudioLoop(client, buffer)) { IsBackground = true, Name = "GameAudioCapture" };
        client.Start();
        _audioThread.Start();
        _audioEncoder.Start();
    }

    private void AudioLoop(AudioClient client, BufferedWaveProvider buffer)
    {
        var capture = client.AudioCaptureClient;
        var scratch = new byte[CaptureFormat.AverageBytesPerSecond];
        while (_audioRunning)
        {
            try
            {
                int packet = capture.GetNextPacketSize();
                while (packet > 0)
                {
                    var ptr = capture.GetBuffer(out int frames, out var flags);
                    int bytes = frames * CaptureFormat.BlockAlign;
                    if (bytes > scratch.Length) scratch = new byte[bytes];
                    if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(scratch, 0, bytes);
                    else Marshal.Copy(ptr, scratch, 0, bytes);
                    capture.ReleaseBuffer(frames);
                    buffer.AddSamples(scratch, 0, bytes);
                    packet = capture.GetNextPacketSize();
                }
            }
            catch { break; }   // game exited / device gone — video carries on
            Thread.Sleep(10);
        }
    }

    private void StopAudio()
    {
        _audioRunning = false;
        _audioEncoder?.Stop();
        _audioThread?.Join(500);
        try { _audioClient?.Stop(); } catch { }
        _audioClient?.Dispose();
        _audioClient = null;
        _audioThread = null;
        _audioEncoder = null;
    }

    public void Dispose()
    {
        Stop();
        (_device as IDisposable)?.Dispose();
        _device = null;
    }
}
#else
namespace FatGuysSpeak.Client.Services;

public sealed class GameCaptureService : IDisposable
{
    public event Action<byte[]>? FrameCaptured;
    public event Action<byte[]>? StreamAudioCaptured;
    public event Action? TargetClosed;
    public event Action? GameSoundBlocked;
    public bool IsCapturing => false;
    public void Start(IntPtr hwnd, int processId, bool gameSound, int fps = 20) { }
    public void SwitchTarget(IntPtr hwnd, int processId) { }
    public void SetGameSound(bool on) { }
    public void UpdateQuality(int fps, int jpegQuality, int maxWidth) { }
    public void Stop() { }
    public void Dispose() { }
}
#endif
