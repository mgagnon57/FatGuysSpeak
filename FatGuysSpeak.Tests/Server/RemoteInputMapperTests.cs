using FatGuysSpeak.Shared;
using Xunit;

namespace FatGuysSpeak.Tests.Server;

public class RemoteInputMapperTests
{
    [Fact]
    public void Center_OfFullDesktop_MapsToScreenCenter()
    {
        var (x, y) = RemoteInputMapper.ToScreenPixel(0.5, 0.5, 0, 0, 1920, 1080);
        Assert.Equal(960, x);
        Assert.Equal(540, y);
    }

    [Fact]
    public void Origin_OfWindowRect_MapsToWindowTopLeft()
    {
        var (x, y) = RemoteInputMapper.ToScreenPixel(0, 0, 100, 200, 800, 600);
        Assert.Equal(100, x);
        Assert.Equal(200, y);
    }

    [Fact]
    public void FarCorner_OfWindowRect_MapsToWindowBottomRight()
    {
        var (x, y) = RemoteInputMapper.ToScreenPixel(1, 1, 100, 200, 800, 600);
        Assert.Equal(900, x);
        Assert.Equal(800, y);
    }

    [Fact]
    public void OutOfRange_IsClampedToRect()
    {
        var (x, y) = RemoteInputMapper.ToScreenPixel(1.5, -0.3, 0, 0, 1000, 1000);
        Assert.Equal(1000, x);
        Assert.Equal(0, y);
    }
}
