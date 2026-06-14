#if WINDOWS
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace FatGuysSpeak.Client.Services;

public sealed class CameraService
{
    public event Action<byte[]>? FrameCaptured;
    public bool IsCapturing { get; private set; }

    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private int _busy;

    private const int TargetWidth  = 320;
    private const int TargetHeight = 240;

    public static async Task<List<(string Id, string Name)>> GetCamerasAsync()
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        return [.. devices.Select(d => (d.Id, d.Name))];
    }

    public async Task StartAsync(string? deviceId = null)
    {
        if (IsCapturing) return;

        if (deviceId is null)
        {
            var cams = await GetCamerasAsync();
            if (cams.Count == 0) throw new InvalidOperationException("No webcam found on this device.");
            deviceId = cams[0].Id;
        }

        _capture = new MediaCapture();
        await _capture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            VideoDeviceId         = deviceId,
            StreamingCaptureMode  = StreamingCaptureMode.Video,
            SharingMode           = MediaCaptureSharingMode.ExclusiveControl,
            MemoryPreference      = MediaCaptureMemoryPreference.Cpu,
        });

        var source = _capture.FrameSources.Values
            .FirstOrDefault(fs =>
                fs.Info.MediaStreamType == MediaStreamType.VideoRecord &&
                fs.Info.SourceKind == MediaFrameSourceKind.Color)
            ?? throw new InvalidOperationException("No color video source found on this camera.");

        // Prefer a small capture resolution to reduce encode cost
        var fmt = source.SupportedFormats
            .Where(f => f.VideoFormat.Width <= 640 && f.VideoFormat.Height <= 480
                     && f.Subtype == MediaEncodingSubtypes.Yuy2)
            .OrderByDescending(f => f.VideoFormat.Width * f.VideoFormat.Height)
            .FirstOrDefault()
            ?? source.SupportedFormats
            .Where(f => f.VideoFormat.Width <= 640 && f.VideoFormat.Height <= 480)
            .OrderByDescending(f => f.VideoFormat.Width * f.VideoFormat.Height)
            .FirstOrDefault();

        if (fmt is not null) await source.SetFormatAsync(fmt);

        _reader = await _capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
        _reader.FrameArrived += OnFrameArrived;

        var status = await _reader.StartAsync();
        if (status != MediaFrameReaderStartStatus.Success)
            throw new InvalidOperationException($"Camera reader failed to start: {status}");

        IsCapturing = true;
        _busy = 0;
    }

    public async Task StopAsync()
    {
        IsCapturing = false;
        if (_reader is not null)
        {
            _reader.FrameArrived -= OnFrameArrived;
            await _reader.StopAsync();
            _reader.Dispose();
            _reader = null;
        }
        _capture?.Dispose();
        _capture = null;
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;

        SoftwareBitmap? copy = null;
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var bmp = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (bmp is null) { Interlocked.Exchange(ref _busy, 0); return; }
            copy = SoftwareBitmap.Copy(bmp);
        }
        catch { Interlocked.Exchange(ref _busy, 0); return; }

        _ = EncodeAndDispatchAsync(copy);
    }

    private async Task EncodeAndDispatchAsync(SoftwareBitmap bitmap)
    {
        try
        {
            var src = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                ? bitmap
                : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
            encoder.SetSoftwareBitmap(src);
            encoder.BitmapTransform.ScaledWidth  = TargetWidth;
            encoder.BitmapTransform.ScaledHeight = TargetHeight;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Linear;
            await encoder.FlushAsync();

            var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[stream.Size];
            reader.ReadBytes(bytes);

            FrameCaptured?.Invoke(bytes);
        }
        catch { }
        finally
        {
            bitmap.Dispose();
            Interlocked.Exchange(ref _busy, 0);
        }
    }
}

#else

namespace FatGuysSpeak.Client.Services;

public sealed class CameraService
{
    public event Action<byte[]>? FrameCaptured;
    public bool IsCapturing { get; private set; }
    public static Task<List<(string Id, string Name)>> GetCamerasAsync() =>
        Task.FromResult(new List<(string, string)>());
    public Task StartAsync(string? deviceId = null) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
}

#endif
