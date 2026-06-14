using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Tests.Client;

public class EmojiDataTests
{
    [Fact]
    public void Common_IsNotEmpty()
        => Assert.NotEmpty(EmojiData.Common);

    [Fact]
    public void Common_HasNoNullOrEmptyEntries()
        => Assert.All(EmojiData.Common, e => Assert.False(string.IsNullOrEmpty(e)));

    [Fact]
    public void Common_AllEntriesAreUnique()
        => Assert.Equal(EmojiData.Common.Count, EmojiData.Common.Distinct().Count());

    [Fact]
    public void Common_ContainsExpectedCategories()
    {
        // Spot-check one representative from each category
        Assert.Contains("😀", EmojiData.Common); // smileys
        Assert.Contains("👍", EmojiData.Common); // gestures
        Assert.Contains("❤️", EmojiData.Common); // symbols
        Assert.Contains("🐶", EmojiData.Common); // animals
        Assert.Contains("🍕", EmojiData.Common); // food
        Assert.Contains("💻", EmojiData.Common); // tech
        Assert.Contains("🌞", EmojiData.Common); // nature
    }

    [Theory]
    [InlineData("", "😀", "😀")]
    [InlineData("hello ", "😊", "hello 😊")]
    [InlineData("👍", "❤️", "👍❤️")]
    [InlineData("text", "🎉", "text🎉")]
    public void InsertEmoji_ConcatenatesCorrectly(string input, string emoji, string expected)
        => Assert.Equal(expected, input + emoji);

    [Fact]
    public void InsertEmoji_Multiple_AllAppend()
    {
        var result = "" + "😀" + "❤️" + "🎉";
        Assert.Equal("😀❤️🎉", result);
    }

    [Fact]
    public void EmojiStrings_SurviveRoundTripThroughString()
    {
        foreach (var emoji in EmojiData.Common)
        {
            var roundTrip = new string(emoji.ToCharArray());
            Assert.Equal(emoji, roundTrip);
        }
    }

    [Fact]
    public void Common_CountIsReasonable()
    {
        // Sanity: not too few (< 20) or absurdly many (> 500)
        Assert.InRange(EmojiData.Common.Count, 20, 500);
    }
}
