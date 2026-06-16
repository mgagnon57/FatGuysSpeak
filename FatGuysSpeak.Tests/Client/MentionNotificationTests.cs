using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Tests.Client;

public class MentionNotificationTests
{
    [Fact]
    public void IsMentionOf_AtUsername_ReturnsTrue()
    {
        Assert.True(NotificationRules.IsMentionOf("Hey @alice check this out", "alice"));
    }

    [Fact]
    public void IsMentionOf_CaseInsensitive_ReturnsTrue()
    {
        Assert.True(NotificationRules.IsMentionOf("Hey @ALICE, what's up?", "alice"));
    }

    [Fact]
    public void IsMentionOf_NoAtSign_ReturnsFalse()
    {
        Assert.False(NotificationRules.IsMentionOf("Hey alice, what's up?", "alice"));
    }

    [Fact]
    public void IsMentionOf_NullContent_ReturnsFalse()
    {
        Assert.False(NotificationRules.IsMentionOf(null, "alice"));
    }

    [Fact]
    public void IsMentionOf_NullUsername_ReturnsFalse()
    {
        Assert.False(NotificationRules.IsMentionOf("Hey @alice", null));
    }

    [Fact]
    public void IsMentionOf_EmptyContent_ReturnsFalse()
    {
        Assert.False(NotificationRules.IsMentionOf("", "alice"));
    }

    [Fact]
    public void IsMentionOf_EmptyUsername_ReturnsFalse()
    {
        Assert.False(NotificationRules.IsMentionOf("@", ""));
    }

    [Theory]
    [InlineData("Hello @alice and @bob", "alice", true)]
    [InlineData("Hello @alice and @bob", "bob",   true)]
    [InlineData("Hello @alice and @bob", "charlie", false)]
    [InlineData("@ALICE uppercase mention", "alice", true)]
    [InlineData("no mention here", "alice", false)]
    public void IsMentionOf_Theory(string content, string username, bool expected)
    {
        Assert.Equal(expected, NotificationRules.IsMentionOf(content, username));
    }
}
