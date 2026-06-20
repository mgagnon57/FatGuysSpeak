using FatGuysSpeak.Server.Services;
using Microsoft.Extensions.Configuration;

namespace FatGuysSpeak.Tests.Server;

public class TtsServiceTests
{
    private static IConfiguration Config(Dictionary<string, string?> kv) =>
        new ConfigurationBuilder().AddInMemoryCollection(kv).Build();

    [Fact]
    public void ResolveVoiceIds_CombinesArrayAndSingle_Deduped()
    {
        var cfg = Config(new()
        {
            ["ElevenLabs:VoiceIds:0"] = "voiceA",
            ["ElevenLabs:VoiceIds:1"] = "voiceB",
            ["ElevenLabs:VoiceId"]    = "voiceA",   // duplicate of the array entry
        });

        var ids = TtsService.ResolveVoiceIds(cfg);

        Assert.Equal(new[] { "voiceA", "voiceB" }, ids);   // deduped, single folded in
    }

    [Fact]
    public void ResolveVoiceIds_FallsBackToSingleVoiceId()
    {
        var ids = TtsService.ResolveVoiceIds(Config(new() { ["ElevenLabs:VoiceId"] = "only-one" }));
        Assert.Equal(new[] { "only-one" }, ids);
    }

    [Fact]
    public void ResolveVoiceIds_NoneConfigured_ReturnsEmpty()
    {
        Assert.Empty(TtsService.ResolveVoiceIds(Config(new())));
    }

    [Fact]
    public void Resample_24kTo48k_DoublesLengthAndKeepsKeyframes()
    {
        var input = new short[] { 100, 200, 300, 400 };

        var output = TtsService.Resample(input, 24000, 48000);

        Assert.Equal(8, output.Length);          // 2x upsample
        Assert.Equal(100, output[0]);            // original samples land on even indices
        Assert.Equal(200, output[2]);
        Assert.Equal(300, output[4]);
        Assert.Equal(150, output[1]);            // midpoint interpolated between 100 and 200
    }

    [Fact]
    public void Resample_SameRate_ReturnsInputUnchanged()
    {
        var input = new short[] { 1, 2, 3 };
        Assert.Same(input, TtsService.Resample(input, 48000, 48000));
    }

    [Fact]
    public void BytesToShorts_RoundTrips16BitLittleEndianPcm()
    {
        var orig = new short[] { 0, 100, -200, 32000, -32000 };
        var bytes = new byte[orig.Length * 2];
        Buffer.BlockCopy(orig, 0, bytes, 0, bytes.Length);

        Assert.Equal(orig, TtsService.BytesToShorts(bytes));
    }
}
