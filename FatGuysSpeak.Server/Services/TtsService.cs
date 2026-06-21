using Concentus;
using Concentus.Enums;
using FatGuysSpeak.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace FatGuysSpeak.Server.Services;

/// <summary>
/// Gives PorkChop a real voice: synthesizes text with ElevenLabs and streams it into a voice channel
/// as Opus frames matching the client pipeline (48 kHz mono, 20 ms). No-op unless an ElevenLabs API
/// key and voice id are configured (ElevenLabs:ApiKey / ElevenLabs:VoiceId / ElevenLabs:Model).
/// </summary>
public class TtsService(IHttpClientFactory httpFactory, IConfiguration config, IHubContext<ChatHub> hub, ILogger<TtsService> logger)
{
    private readonly string   _apiKey   = config["ElevenLabs:ApiKey"] ?? "";
    private readonly string[] _voiceIds = ResolveVoiceIds(config);   // PorkChop picks one at random per line
    private readonly string   _model    = config["ElevenLabs:Model"] ?? "eleven_turbo_v2_5";

    private const int ElevenRate   = 24000;   // request PCM from ElevenLabs at 24 kHz
    private const int VoiceRate    = 48000;   // FatGuysSpeak voice pipeline sample rate
    private const int FrameSamples = 960;     // 20 ms @ 48 kHz, matching the client

    public bool Enabled => !string.IsNullOrEmpty(_apiKey) && _voiceIds.Length > 0;

    // Combine the VoiceIds array and the single VoiceId (backward compat), deduped, non-empty.
    public static string[] ResolveVoiceIds(IConfiguration config)
    {
        var ids = config.GetSection("ElevenLabs:VoiceIds").Get<string[]>()?.ToList() ?? [];
        var single = config["ElevenLabs:VoiceId"];
        if (!string.IsNullOrWhiteSpace(single)) ids.Add(single);
        return ids.Select(v => v?.Trim() ?? "").Where(v => v.Length > 0).Distinct().ToArray();
    }

    public async Task SpeakIntoVoiceChannelAsync(int channelId, string text)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(text)) return;
        try
        {
            var pcm24 = await SynthesizeAsync(text);
            if (pcm24 is null || pcm24.Length == 0) return;
            var samples48 = Resample(BytesToShorts(pcm24), ElevenRate, VoiceRate);
            await StreamOpusAsync(channelId, samples48);
        }
        catch (Exception ex) { logger.LogError(ex, "TTS speak failed for channel {ChannelId}", channelId); }
    }

    private async Task<byte[]?> SynthesizeAsync(string text)
    {
        var client = httpFactory.CreateClient("elevenlabs");
        var voiceId = _voiceIds[Random.Shared.Next(_voiceIds.Length)];   // different voice each time
        var req = new HttpRequestMessage(HttpMethod.Post, $"v1/text-to-speech/{voiceId}?output_format=pcm_{ElevenRate}")
        {
            Content = JsonContent.Create(new { text, model_id = _model }),
        };
        req.Headers.Add("xi-api-key", _apiKey);

        var res = await client.SendAsync(req);
        if (!res.IsSuccessStatusCode)
        {
            logger.LogWarning("ElevenLabs {Status}: {Body}", (int)res.StatusCode, await res.Content.ReadAsStringAsync());
            return null;
        }
        return await res.Content.ReadAsByteArrayAsync();
    }

    private const int FrameMs = 20;     // each Opus frame is 20 ms of audio
    private const int LeadMs   = 800;   // keep ~0.8 s buffered on the client so timer jitter can't starve it

    // Encode 48 kHz mono PCM to Opus and push it to the voice group. We burst-fill the client's
    // playback buffer up to a lead cushion, then pace off a stopwatch so the average rate stays
    // real-time without accumulating Task.Delay overshoot (which was starving the buffer and making
    // the voice choppy). The client's BufferedWaveProvider handles final playback timing.
    public const int VoiceSampleRate = VoiceRate;   // so callers (soundboard) can resample to match

    /// <summary>Stream already-48kHz-mono PCM into a voice channel as Opus (used by the soundboard).</summary>
    public Task PlaySamples48Async(int channelId, short[] samples48) => StreamOpusAsync(channelId, samples48);

    private async Task StreamOpusAsync(int channelId, short[] samples)
    {
        var encoder = OpusCodecFactory.CreateEncoder(VoiceRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        var group = hub.Clients.Group($"voice-{channelId}");
        var buf = new byte[4000];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var sent = 0;
        for (var off = 0; off + FrameSamples <= samples.Length; off += FrameSamples)
        {
            var frame = new short[FrameSamples];
            Array.Copy(samples, off, frame, 0, FrameSamples);
            var len = encoder.Encode(frame, FrameSamples, buf, buf.Length);
            if (len <= 0) continue;

            await group.SendAsync("ReceiveVoiceData", buf[..len]);
            sent++;

            // How far ahead of real-time we've sent. Only wait once we're past the lead cushion.
            var aheadMs = (long)sent * FrameMs - clock.ElapsedMilliseconds;
            if (aheadMs > LeadMs)
                await Task.Delay((int)(aheadMs - LeadMs));
        }
    }

    // ── pure audio helpers (unit-tested) ───────────────────────────────────────
    public static short[] BytesToShorts(byte[] pcm)
    {
        var s = new short[pcm.Length / 2];
        Buffer.BlockCopy(pcm, 0, s, 0, s.Length * 2);   // 16-bit little-endian PCM
        return s;
    }

    public static short[] Resample(short[] input, int inRate, int outRate)
    {
        if (inRate == outRate || input.Length == 0) return input;
        var outLen = (int)((long)input.Length * outRate / inRate);
        var output = new short[outLen];
        var step = (double)inRate / outRate;
        for (var i = 0; i < outLen; i++)
        {
            var pos  = i * step;
            var idx  = (int)pos;
            var frac = pos - idx;
            var a = input[Math.Min(idx, input.Length - 1)];
            var b = input[Math.Min(idx + 1, input.Length - 1)];
            output[i] = (short)(a + (b - a) * frac);
        }
        return output;
    }
}
