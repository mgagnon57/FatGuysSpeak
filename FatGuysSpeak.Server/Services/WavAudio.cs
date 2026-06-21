namespace FatGuysSpeak.Server.Services;

/// <summary>Minimal, cross-platform WAV reader for the soundboard. Decodes 16-bit PCM WAV (mono or
/// multi-channel) into mono samples plus the source sample rate. Returns null for anything it can't
/// handle (compressed formats, non-16-bit, malformed) so callers can reject the upload cleanly.</summary>
public static class WavAudio
{
    public static (short[] samples, int sampleRate)? DecodeToMono(byte[] wav)
    {
        if (wav.Length < 44) return null;
        if (wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F') return null;
        if (wav[8] != 'W' || wav[9] != 'A' || wav[10] != 'V' || wav[11] != 'E') return null;

        int channels = 0, sampleRate = 0, bits = 0, audioFormat = 0;
        int dataOff = -1, dataLen = 0;

        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
            var size = BitConverter.ToInt32(wav, pos + 4);
            var body = pos + 8;
            if (size < 0 || body + size > wav.Length) size = wav.Length - body;   // tolerate a bad/oversized length

            if (id == "fmt " && size >= 16)
            {
                audioFormat = BitConverter.ToInt16(wav, body);
                channels    = BitConverter.ToInt16(wav, body + 2);
                sampleRate  = BitConverter.ToInt32(wav, body + 4);
                bits        = BitConverter.ToInt16(wav, body + 14);
            }
            else if (id == "data")
            {
                dataOff = body;
                dataLen = size;
            }
            pos = body + size + (size & 1);   // chunks are word-aligned
        }

        if (audioFormat != 1 || bits != 16 || channels < 1 || sampleRate <= 0 || dataOff < 0 || dataLen <= 0)
            return null;

        var sampleCount = dataLen / 2;
        var raw = new short[sampleCount];
        Buffer.BlockCopy(wav, dataOff, raw, 0, sampleCount * 2);   // 16-bit little-endian

        if (channels == 1) return (raw, sampleRate);

        var frames = sampleCount / channels;
        var mono = new short[frames];
        for (var f = 0; f < frames; f++)
        {
            var sum = 0;
            for (var c = 0; c < channels; c++) sum += raw[f * channels + c];
            mono[f] = (short)(sum / channels);
        }
        return (mono, sampleRate);
    }
}
