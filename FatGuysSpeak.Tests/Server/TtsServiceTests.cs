using FatGuysSpeak.Server.Services;

namespace FatGuysSpeak.Tests.Server;

public class TtsServiceTests
{
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
