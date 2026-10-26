using Squelch.Shared;

namespace Squelch.Tests.Client;

// The tray icon shows voice state (idle / muted / live mic level). The model must not hand back a new icon on
// every mic-level event — GDI icon swaps are costly, so level-only changes are capped at ~12 fps.
public class TrayIconModelTests
{
    private static readonly TrayIconChoice Idle = new(TrayVisual.Idle, 0);

    [Fact]
    public void NotInVoice_IsIdle()
        => Assert.Equal(Idle, new TrayIconModel().Update(false, false, true, 1.0, 0.05, 0));

    [Fact]
    public void Muted_IsMuted_EvenWhileTransmitting()
        => Assert.Equal(new TrayIconChoice(TrayVisual.Muted, 0), new TrayIconModel().Update(true, true, true, 1.0, 0.05, 0));

    [Fact]
    public void NotTransmitting_IsIdle()
        => Assert.Equal(Idle, new TrayIconModel().Update(true, false, false, 1.0, 0.05, 0));

    [Fact]
    public void FullLevel_Converges_To_Level7()
    {
        var model = new TrayIconModel();
        TrayIconChoice? last = null;
        for (var i = 0; i < 20; i++)
            last = model.Update(true, false, true, 1.0, 0.05, i * 100L) ?? last;
        Assert.Equal(new TrayIconChoice(TrayVisual.Level, 7), last);
    }

    [Fact]
    public void BelowGate_IsIdle()
        => Assert.Equal(Idle, new TrayIconModel().Update(true, false, true, 0.02, 0.05, 0));

    [Fact]
    public void SameChoice_ReturnsNull()
    {
        var model = new TrayIconModel();
        Assert.NotNull(model.Update(false, false, true, 0, 0.05, 0));
        Assert.Null(model.Update(false, false, true, 0, 0.05, 100));
    }

    [Fact]
    public void LevelChanges_ThrottledTo80ms()
    {
        var model = new TrayIconModel();
        var returned = 0;
        for (var t = 0; t <= 1000; t += 10)
            if (model.Update(true, false, true, t / 10 % 2 == 0 ? 0.3 : 0.9, 0.05, t) is not null) returned++;
        Assert.InRange(returned, 1, 13);
    }

    [Fact]
    public void MuteToggle_NotThrottled()
    {
        var model = new TrayIconModel();
        Assert.Equal(TrayVisual.Level, model.Update(true, false, true, 1.0, 0.05, 0)!.Value.Visual);
        Assert.Equal(new TrayIconChoice(TrayVisual.Muted, 0), model.Update(true, true, true, 1.0, 0.05, 10));
    }
}
