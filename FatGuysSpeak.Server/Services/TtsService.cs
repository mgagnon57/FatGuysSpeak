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
    private readonly string _apiKey  = config["ElevenLabs:ApiKey"]  ?? "";
    private readonly string _voiceId = config["ElevenLabs:VoiceId"] ?? "";
    private readonly string _model   = config["ElevenLabs:Model"]   ?? "eleven_turbo_v2_5";

    private const int ElevenRate   = 24000;   // request PCM from ElevenLabs at 24 kHz
    private const int VoiceRate    = 48000;   // FatGuysSpeak voice pipeline sample rate
    private const int FrameSamples = 960;     // 20 ms @ 48 kHz, matching the client

    public bool Enabled => !string.IsNullOrEmpty(_apiKey) && !string.IsNullOrEmpty(_voiceId);

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
        var req = new HttpRequestMessage(HttpMethod.Post, $"v1/text-to-speech/{_voiceId}?output_format=pcm_{ElevenRate}")
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

    // Encode 48 kHz mono PCM to Opus and push it to the voice group one 20 ms frame at a time,
    // paced in real time so the clients' jitter buffers play it back smoothly.
    private async Task StreamOpusAsync(int channelId, short[] samples)
    {
        var encoder = OpusCodecFactory.CreateEncoder(VoiceRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        var group = hub.Clients.Group($"voice-{channelId}");
        var buf = new byte[4000];
        for (var off = 0; off + FrameSamples <= samples.Length; off += FrameSamples)
        {
            var frame = new short[FrameSamples];
            Array.Copy(samples, off, frame, 0, FrameSamples);
            var len = encoder.Encode(frame, FrameSamples, buf, buf.Length);
            if (len > 0)
            {
                await group.SendAsync("ReceiveVoiceData", buf[..len]);
                await Task.Delay(20);
            }
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
