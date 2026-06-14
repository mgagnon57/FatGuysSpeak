using Concentus;
using Concentus.Enums;
using NAudio.Wave;

namespace FatGuysSpeak.Client.Services;

public record AudioDevice(int Index, string Name)
{
    public override string ToString() => Name;
}

public class AudioService : IDisposable
{
    private WaveInEvent? _waveIn;
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _playbackBuffer;
    private IOpusEncoder? _encoder;
    private IOpusDecoder? _decoder;
    private System.Threading.Timer? _testMicTimer;
    private double _testMicPhase;
    private bool _loopbackOwnedPlayback;
    private float _outputVolume;

    private const int SampleRate = 48000;
    private const int Channels = 1;
    private const int FrameSizeMs = 20;
    private const int FrameSamples = SampleRate * FrameSizeMs / 1000;
    private const double TestToneHz = 440.0;

    public event Action<byte[]>? AudioCaptured;
    public event Action<double>? MicLevelChanged;

    public bool IsMuted { get; private set; }
    public bool IsDeafened { get; private set; }
    public bool IsTestMicActive { get; private set; }
    public bool IsLoopbackActive { get; private set; }

    public int InputDeviceIndex { get; set; }
    public int OutputDeviceIndex { get; set; }
    public float InputGain { get; set; }
    public float OutputVolume
    {
        get => _outputVolume;
        set
        {
            _outputVolume = value;
            if (_waveOut is not null) _waveOut.Volume = Math.Clamp(value, 0f, 1f);
        }
    }

    public AudioService()
    {
        InputDeviceIndex = Preferences.Get("audio_input_device", 0);
        OutputDeviceIndex = Preferences.Get("audio_output_device", 0);
        InputGain = Preferences.Get("audio_input_gain", 1.0f);
        _outputVolume = Preferences.Get("audio_output_volume", 1.0f);
    }

    public static List<AudioDevice> GetInputDevices()
    {
        var list = new List<AudioDevice>();
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            list.Add(new(i, WaveInEvent.GetCapabilities(i).ProductName));
        return list;
    }

    public static List<AudioDevice> GetOutputDevices()
    {
        var list = new List<AudioDevice>();
        for (int i = 0; i < WaveOut.DeviceCount; i++)
            list.Add(new(i, WaveOut.GetCapabilities(i).ProductName));
        return list;
    }

    public void StartCapture(bool testMic = false)
    {
        IsTestMicActive = testMic;
        _encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP);

        if (testMic)
        {
            _testMicPhase = 0;
            _testMicTimer = new System.Threading.Timer(GenerateTestAudio, null, 0, FrameSizeMs);
        }
        else
        {
            int deviceIndex = Math.Clamp(InputDeviceIndex, 0, Math.Max(0, WaveInEvent.DeviceCount - 1));
            _waveIn = new WaveInEvent { DeviceNumber = deviceIndex, WaveFormat = new WaveFormat(SampleRate, 16, Channels), BufferMilliseconds = FrameSizeMs };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.StartRecording();
        }
    }

    public void StartPlayback()
    {
        _decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);
        _playbackBuffer = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, Channels)) { DiscardOnBufferOverflow = true };
        int deviceIndex = Math.Clamp(OutputDeviceIndex, 0, Math.Max(0, WaveOut.DeviceCount - 1));
        _waveOut = new WaveOutEvent { DeviceNumber = deviceIndex };
        _waveOut.Init(_playbackBuffer);
        _waveOut.Volume = Math.Clamp(_outputVolume, 0f, 1f);
        _waveOut.Play();
    }

    public void StartLoopback()
    {
        if (IsLoopbackActive) return;
        IsLoopbackActive = true;

        if (_waveIn is null && _testMicTimer is null)
        {
            _encoder ??= OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP);
            _waveIn = new WaveInEvent { WaveFormat = new WaveFormat(SampleRate, 16, Channels), BufferMilliseconds = FrameSizeMs };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.StartRecording();
        }

        if (_playbackBuffer is null)
        {
            _playbackBuffer = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, Channels)) { DiscardOnBufferOverflow = true };
            _waveOut = new WaveOutEvent();
            _waveOut.Init(_playbackBuffer);
            _waveOut.Play();
            _loopbackOwnedPlayback = true;
        }
    }

    public void StopLoopback(bool alsoStopCapture = false)
    {
        IsLoopbackActive = false;
        if (alsoStopCapture) StopCapture();
        if (_loopbackOwnedPlayback)
        {
            StopPlayback();
            _loopbackOwnedPlayback = false;
        }
        MicLevelChanged?.Invoke(0);
    }

    public void PlayAudio(byte[] opusData)
    {
        if (IsDeafened || _decoder is null || _playbackBuffer is null) return;

        var pcm = new short[FrameSamples * Channels];
        int decoded = _decoder.Decode(opusData, pcm, FrameSamples, false);
        var bytes = new byte[decoded * 2 * Channels];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        _playbackBuffer.AddSamples(bytes, 0, bytes.Length);
    }

    public void SetMuted(bool muted) => IsMuted = muted;
    public void SetDeafened(bool deafened) => IsDeafened = deafened;

    public void StopCapture()
    {
        _testMicTimer?.Dispose();
        _testMicTimer = null;
        IsTestMicActive = false;
        _waveIn?.StopRecording();
        _waveIn?.Dispose();
        _waveIn = null;
        MicLevelChanged?.Invoke(0);
    }

    public void StopPlayback()
    {
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;
        _playbackBuffer = null;
    }

    private static double ComputeLevel(short[] samples)
    {
        double sum = 0;
        foreach (var s in samples) sum += (double)s * s;
        return Math.Min(1.0, Math.Sqrt(sum / samples.Length) / short.MaxValue * 4.0);
    }

    private void GenerateTestAudio(object? state)
    {
        if (_encoder is null) return;

        var samples = new short[FrameSamples];
        for (int i = 0; i < FrameSamples; i++)
        {
            samples[i] = (short)(Math.Sin(_testMicPhase / SampleRate * 2 * Math.PI * TestToneHz) * short.MaxValue * 0.3);
            _testMicPhase++;
        }

        MicLevelChanged?.Invoke(IsMuted ? 0 : ComputeLevel(samples));

        if (IsMuted) return;

        var encoded = new byte[4000];
        int len = _encoder.Encode(samples, FrameSamples, encoded, encoded.Length);
        if (len > 0)
        {
            var packet = new byte[len];
            Array.Copy(encoded, packet, len);
            AudioCaptured?.Invoke(packet);
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_encoder is null) return;

        var samples = new short[e.BytesRecorded / 2];
        Buffer.BlockCopy(e.Buffer, 0, samples, 0, e.BytesRecorded);

        if (InputGain != 1.0f)
            for (int i = 0; i < samples.Length; i++)
                samples[i] = (short)Math.Clamp(samples[i] * InputGain, short.MinValue, short.MaxValue);

        MicLevelChanged?.Invoke(IsMuted ? 0 : ComputeLevel(samples));

        if (IsLoopbackActive && _playbackBuffer is not null)
            _playbackBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

        if (IsMuted) return;

        var encoded = new byte[4000];
        int len = _encoder.Encode(samples, FrameSamples, encoded, encoded.Length);
        if (len > 0)
        {
            var packet = new byte[len];
            Array.Copy(encoded, packet, len);
            AudioCaptured?.Invoke(packet);
        }
    }

    public void Dispose()
    {
        StopCapture();
        StopPlayback();
    }
}
