#if WINDOWS
using Concentus;
using Concentus.Enums;
using NAudio.Wave;

namespace Squelch.Client.Services;

/// <summary>Pulls 20 ms of 48 kHz mono audio at a time from a sample source and Opus-encodes it into
/// stream audio packets. Only pulls when a full frame is buffered, so silence never blocks video.</summary>
internal sealed class StreamAudioEncoder : IDisposable
{
    public const int SampleRate = 48000;
    private const int FrameSamples = 960; // 20 ms at 48 kHz

    private readonly ISampleProvider _source;
    private readonly Action<byte[]> _onPacket;
    private readonly IOpusEncoder _encoder = OpusCodecFactory.CreateEncoder(SampleRate, 1, OpusApplication.OPUS_APPLICATION_AUDIO);
    private readonly float[] _pullBuf = new float[FrameSamples];
    private readonly short[] _opusBuf = new short[FrameSamples];
    private Timer? _timer;

    public StreamAudioEncoder(ISampleProvider mono48k, Action<byte[]> onPacket)
    {
        if (mono48k.WaveFormat.SampleRate != SampleRate || mono48k.WaveFormat.Channels != 1)
            throw new ArgumentException("Source must be 48 kHz mono.", nameof(mono48k));
        _source = mono48k;
        _onPacket = onPacket;
    }

    public void Start() => _timer ??= new Timer(_ => PullAndEncode(), null, 0, 20);

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void PullAndEncode()
    {
        int read = _source.Read(_pullBuf, 0, FrameSamples);
        if (read < FrameSamples) return;

        for (int i = 0; i < FrameSamples; i++)
            _opusBuf[i] = (short)Math.Clamp(_pullBuf[i] * 32767f, short.MinValue, short.MaxValue);

        try
        {
            var output = new byte[1275];
            int len = _encoder.Encode(_opusBuf, FrameSamples, output, output.Length);
            if (len > 0) _onPacket(output[..len]);
        }
        catch { }
    }

    public void Dispose() => Stop();
}
#endif
