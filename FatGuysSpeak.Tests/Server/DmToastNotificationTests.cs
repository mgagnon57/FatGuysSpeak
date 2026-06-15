using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Tests.Server;

public class DmToastNotificationTests
{
    // ── ShouldToastDm ─────────────────────────────────────────────────────────

    [Fact]
    public void ShouldToastDm_DifferentUser_NotActiveConvo_ReturnsTrue()
    {
        Assert.True(NotificationRules.ShouldToastDm(authorId: 2, currentUserId: 1, isActiveConvo: false),
            "Should toast when message is from another user and the conversation is not active");
    }

    [Fact]
    public void ShouldToastDm_OwnMessage_ReturnsFalse()
    {
        Assert.False(NotificationRules.ShouldToastDm(authorId: 1, currentUserId: 1, isActiveConvo: false),
            "Should not toast own messages");
    }

    [Fact]
    public void ShouldToastDm_DifferentUser_ActiveConvo_ReturnsFalse()
    {
        Assert.False(NotificationRules.ShouldToastDm(authorId: 2, currentUserId: 1, isActiveConvo: true),
            "Should not toast when the conversation is currently open and active");
    }

    [Fact]
    public void ShouldToastDm_OwnMessage_ActiveConvo_ReturnsFalse()
    {
        Assert.False(NotificationRules.ShouldToastDm(authorId: 1, currentUserId: 1, isActiveConvo: true),
            "Should not toast own messages even if conversation is somehow marked active");
    }

    [Theory]
    [InlineData(10, 99, false, true)]
    [InlineData(10, 10, false, false)]
    [InlineData(10, 99, true,  false)]
    [InlineData(10, 10, true,  false)]
    public void ShouldToastDm_Theory(int authorId, int currentUserId, bool isActiveConvo, bool expected)
    {
        Assert.Equal(expected, NotificationRules.ShouldToastDm(authorId, currentUserId, isActiveConvo));
    }
}
