using FatGuysSpeak.Server.Services;

namespace FatGuysSpeak.Tests.Server;

public class OnlineTimeTrackerTests
{
    [Fact]
    public void SingleSession_AddsElapsedSeconds()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t = new OnlineTimeTracker(() => now);
        t.Connect(1);
        now = now.AddSeconds(90);
        Assert.Equal(90, t.Disconnect(1));
    }

    [Fact]
    public void OverlappingConnections_CountWallClockOnce()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t = new OnlineTimeTracker(() => now);
        t.Connect(1);
        now = now.AddSeconds(10);
        t.Connect(1);
        now = now.AddSeconds(10);
        Assert.Equal(0, t.Disconnect(1));
        now = now.AddSeconds(10);
        Assert.Equal(30, t.Disconnect(1));
    }

    [Fact]
    public void UnmatchedDisconnect_ReturnsZero()
    {
        var t = new OnlineTimeTracker(() => DateTime.UtcNow);
        Assert.Equal(0, t.Disconnect(99));
    }

    [Fact]
    public void LiveSeconds_ReturnsInProgressWhenOnline_ZeroWhenOffline()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var t = new OnlineTimeTracker(() => now);
        Assert.Equal(0, t.LiveSeconds(1));
        t.Connect(1);
        now = now.AddSeconds(45);
        Assert.Equal(45, t.LiveSeconds(1));
        t.Disconnect(1);
        Assert.Equal(0, t.LiveSeconds(1));
    }
}
