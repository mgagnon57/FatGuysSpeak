using System.Text;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.Ggml;

namespace FatGuysSpeak.Client.Services;

public class SpeechService : IDisposable
{
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private WaveInEvent? _waveIn;

    private readonly List<float> _buffer = [];
    private readonly object _bufferLock = new();
    private readonly SemaphoreSlim _transcribeLock = new(1, 1);

    private bool _isSpeaking;
    private bool _modelReady;

    // Whisper expects 16 kHz mono float32
    private const int SampleRate = 16000;
    private const int FrameMs = 20;
    private const float SpeechRmsThreshold = 0.015f;

    private static readonly string[] Hallucinations =
        ["[blank_audio]", "[music]", "[silence]", "thank you.", "thanks for watching.", "(music)", "you"];

    public bool IsListening { get; private set; }
    public event Action<string>? TextRecognized;
    public event Action<string>? HypothesisChanged;

    private static string ModelPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FatGuysSpeak", "ggml-base.en.bin");

    public async Task StartListeningAsync()
    {
        if (IsListening) return;

        if (!_modelReady)
        {
            await EnsureModelAsync();
            _modelReady = true;
        }

        if (_factory is null) return; // download failed

        lock (_bufferLock) { _buffer.Clear(); _isSpeaking = false; }

        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = FrameMs
        };
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.StartRecording();
        IsListening = true;
    }

    // Called on PTT release: stop capture and transcribe whatever is buffered.
    public async Task StopAndFlushAsync()
    {
        if (!IsListening) return;
        _waveIn?.StopRecording();
        _waveIn?.Dispose();
        _waveIn = null;
        IsListening = false;

        float[]? chunk = null;
        lock (_bufferLock)
        {
            if (_buffer.Count > SampleRate / 2)
                chunk = _buffer.ToArray();
            _buffer.Clear();
            _isSpeaking = false;
        }

        if (chunk is not null)
            await TranscribeAsync(chunk, waitIfBusy: true);
        else
            HypothesisChanged?.Invoke(string.Empty);
    }

    private async Task EnsureModelAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);

        if (!File.Exists(ModelPath))
        {
            HypothesisChanged?.Invoke("Downloading Whisper model (~142 MB, first run only)…");
            try
            {
                using var stream = await WhisperGgmlDownloader.GetGgmlModelAsync(GgmlType.BaseEn);
                using var file = File.OpenWrite(ModelPath);
                await stream.CopyToAsync(file);
            }
            catch (Exception ex)
            {
                HypothesisChanged?.Invoke($"Model download failed: {ex.Message}");
                return;
            }
        }

        HypothesisChanged?.Invoke(string.Empty);
        _factory = WhisperFactory.FromPath(ModelPath);
        _processor = _factory.CreateBuilder().WithLanguage("en").Build();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var shorts = new short[e.BytesRecorded / 2];
        Buffer.BlockCopy(e.Buffer, 0, shorts, 0, e.BytesRecorded);

        var floats = new float[shorts.Length];
        double rmsSum = 0;
        for (int i = 0; i < shorts.Length; i++)
        {
            floats[i] = shorts[i] / 32768f;
            rmsSum += floats[i] * floats[i];
        }
        var rms = (float)Math.Sqrt(rmsSum / floats.Length);

        lock (_bufferLock)
        {
            if (rms > SpeechRmsThreshold)
            {
                _isSpeaking = true;
                _buffer.AddRange(floats);
                HypothesisChanged?.Invoke("Listening…");
            }
            else if (_isSpeaking)
            {
                // Keep buffering straight through pauses. One push-to-talk hold = one message;
                // the whole buffer is transcribed when PTT is released (StopAndFlushAsync). We do
                // NOT split mid-speech on silence — that was what chopped sentences into messages.
                _buffer.AddRange(floats);
            }

            // Hard cap: 120 s of buffered audio per push.
            if (_buffer.Count > SampleRate * 120)
                _buffer.RemoveRange(0, _buffer.Count - SampleRate * 120);
        }
    }

    private async Task TranscribeAsync(float[] samples, bool waitIfBusy = false)
    {
        if (_processor is null || samples.Length < SampleRate / 2)
        {
            HypothesisChanged?.Invoke(string.Empty);
            return;
        }

        // VAD mid-segment calls use non-blocking; PTT flush waits up to 10 s
        bool acquired = waitIfBusy
            ? await _transcribeLock.WaitAsync(TimeSpan.FromSeconds(10))
            : await _transcribeLock.WaitAsync(0);

        if (!acquired)
        {
            HypothesisChanged?.Invoke(string.Empty);
            return;
        }

        try
        {
            HypothesisChanged?.Invoke("Transcribing…");
            using var wav = BuildWavStream(samples);
            await foreach (var seg in _processor.ProcessAsync(wav))
            {
                var text = seg.Text.Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (text.StartsWith('[') && text.EndsWith(']')) continue;
                if (Hallucinations.Any(h => text.Equals(h, StringComparison.OrdinalIgnoreCase))) continue;
                TextRecognized?.Invoke(text);
            }
        }
        finally
        {
            HypothesisChanged?.Invoke(string.Empty);
            _transcribeLock.Release();
        }
    }

    // Build a minimal in-memory 16-bit PCM WAV for the Whisper stream API
    private static MemoryStream BuildWavStream(float[] samples)
    {
        const int channels = 1;
        const int bitsPerSample = 16;
        int byteRate = SampleRate * channels * bitsPerSample / 8;
        short blockAlign = channels * bitsPerSample / 8;
        int dataSize = samples.Length * blockAlign;

        var ms = new MemoryStream(44 + dataSize);
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        bw.Write("RIFF"u8.ToArray());
        bw.Write(36 + dataSize);
        bw.Write("WAVE"u8.ToArray());
        bw.Write("fmt "u8.ToArray());
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)channels);
        bw.Write(SampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write((short)bitsPerSample);
        bw.Write("data"u8.ToArray());
        bw.Write(dataSize);
        foreach (var s in samples)
            bw.Write((short)Math.Clamp((int)(s * 32767), short.MinValue, short.MaxValue));

        ms.Position = 0;
        return ms;
    }

    public void StopListening()
    {
        _waveIn?.StopRecording();
        _waveIn?.Dispose();
        _waveIn = null;
        IsListening = false;
        lock (_bufferLock) { _buffer.Clear(); _isSpeaking = false; }
        HypothesisChanged?.Invoke(string.Empty);
    }

    public void Dispose()
    {
        StopListening();
        _processor?.Dispose();
        _factory?.Dispose();
        _transcribeLock.Dispose();
    }
}
