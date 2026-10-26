using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Tests.Client;

// Legal requirement: Stream Game's "game sound" must never carry voice chat. Process loopback includes the
// target's whole process tree, so a target that is FatGuysSpeak itself, or any process that launched it
// (explorer, a terminal), would rebroadcast the voice chat.
public class GameAudioPolicyTests
{
    private const int Self = 500, Explorer = 100, Terminal = 300, Wardogs = 900, Steam = 200;

    // explorer(100) → terminal(300) → FatGuysSpeak(500);  steam(200) → Wardogs(900)
    private static int? ParentOf(int pid) => pid switch
    {
        Self => Terminal, Terminal => Explorer, Wardogs => Steam, _ => null,
    };

    [Fact]
    public void AllowsAudio_RealGame_True()
        => Assert.True(GameAudioPolicy.AllowsAudio(Wardogs, "WardogsClient-Win64-Shipping", Self, ParentOf));

    [Fact]
    public void AllowsAudio_FatGuysSpeakItself_False()
        => Assert.False(GameAudioPolicy.AllowsAudio(Self, "FatGuysSpeak.Client", Self, ParentOf));

    [Theory]
    [InlineData(Explorer, "explorer")]
    [InlineData(Terminal, "WindowsTerminal")]
    public void AllowsAudio_AncestorOfFatGuysSpeak_False(int pid, string name)
        => Assert.False(GameAudioPolicy.AllowsAudio(pid, name, Self, ParentOf));

    [Theory]
    [InlineData("Discord")]
    [InlineData("ms-teams")]
    [InlineData("Zoom")]
    [InlineData("ts3client_win64")]
    [InlineData("mumble")]
    public void AllowsAudio_VoiceChatApps_False(string name)
        => Assert.False(GameAudioPolicy.AllowsAudio(4242, name, Self, ParentOf));

    [Fact]
    public void AllowsAudio_ParentCycle_DoesNotHang()
        => Assert.True(GameAudioPolicy.AllowsAudio(Wardogs, "Wardogs", Self, pid => pid == Self ? Self : null));
}
