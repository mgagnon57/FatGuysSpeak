using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Server.Services;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Tests.Server;

// Voice must never be transcribed or stored, for anyone (legal requirement). The server enforces it
// at ingestion and scrubs anything older builds already collected.
public class VoiceTranscriptionBanTests : IDisposable
{
    private readonly TestDb _testDb;
    public VoiceTranscriptionBanTests() => _testDb = new TestDb();
    public void Dispose() => _testDb.Dispose();

    private MessagesController Messages(int userId)
    {
        var c = new MessagesController(_testDb.Db, TestHelpers.MockHub(), new ServerMetricsService(),
            TestHelpers.NullBot(), TestHelpers.NullAutomod(), TestHelpers.NullWebhooks());
        TestHelpers.SetUser(c, userId);
        return c;
    }

    private async Task<(int channelId, User user)> SeedAsync()
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "alice");   // PrivateMode off
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);
        return (channel.Id, user);
    }

    [Fact]
    public async Task VoiceMessage_FromAnyUser_IsDroppedAtIngestion()
    {
        var (channelId, user) = await SeedAsync();

        var result = await Messages(user.Id).SendMessage(channelId,
            new SendMessageRequest("an old client's whisper transcription", MessageSource.Voice));

        Assert.IsType<NoContentResult>(result.Result);
        Assert.False(await _testDb.Db.Messages.AnyAsync(m => m.Source == MessageSource.Voice));
        Assert.False(await _testDb.Db.Messages.AnyAsync(m => m.Content.Contains("whisper transcription")));
    }

    [Fact]
    public async Task TextMessage_IsStillStored()
    {
        var (channelId, user) = await SeedAsync();

        await Messages(user.Id).SendMessage(channelId, new SendMessageRequest("typed is fine", MessageSource.Text));

        Assert.True(await _testDb.Db.Messages.AnyAsync(m => m.Content == "typed is fine" && m.Source == MessageSource.Text));
    }

    [Fact]
    public async Task Purge_DeletesVoiceTranscriptsAndVoiceRecaps_KeepsEverythingElse()
    {
        var (channelId, user) = await SeedAsync();
        var spoken = new Message { Content = "spoken thing", AuthorId = user.Id, ChannelId = channelId, Source = MessageSource.Voice };
        var typed  = new Message { Content = "typed thing",  AuthorId = user.Id, ChannelId = channelId, Source = MessageSource.Text };
        _testDb.Db.Messages.AddRange(spoken, typed);
        await _testDb.Db.SaveChangesAsync();

        var reply = new Message { Content = "replying to it", AuthorId = user.Id, ChannelId = channelId, Source = MessageSource.Text, ReplyToId = spoken.Id };
        _testDb.Db.Messages.Add(reply);
        _testDb.Db.MessageReactions.Add(new MessageReaction { MessageId = spoken.Id, UserId = user.Id, Emoji = "😂" });
        _testDb.Db.PinnedMessages.Add(new PinnedMessage { MessageId = spoken.Id, ChannelId = channelId, PinnedById = user.Id });
        var day = DateTime.UtcNow.Date.AddDays(-1);
        _testDb.Db.DailyChatSummaries.AddRange(
            new DailyChatSummary { ChannelId = channelId, Date = day, Source = MessageSource.Voice, Summary = "they talked about X" },
            new DailyChatSummary { ChannelId = channelId, Date = day, Source = MessageSource.Text, Summary = "typed recap" });
        await _testDb.Db.SaveChangesAsync();

        var purged = await VoiceTranscriptPurge.RunAsync(_testDb.Db);
        _testDb.Db.ChangeTracker.Clear();

        Assert.Equal(2, purged);   // one transcript + one voice recap
        Assert.False(await _testDb.Db.Messages.AnyAsync(m => m.Source == MessageSource.Voice));
        Assert.False(await _testDb.Db.DailyChatSummaries.AnyAsync(s => s.Source == MessageSource.Voice));
        Assert.False(await _testDb.Db.MessageReactions.AnyAsync());
        Assert.False(await _testDb.Db.PinnedMessages.AnyAsync());
        Assert.True(await _testDb.Db.Messages.AnyAsync(m => m.Id == typed.Id));
        Assert.True(await _testDb.Db.DailyChatSummaries.AnyAsync(s => s.Source == MessageSource.Text));
        var keptReply = await _testDb.Db.Messages.SingleAsync(m => m.Id == reply.Id);
        Assert.Null(keptReply.ReplyToId);
    }

    [Fact]
    public async Task Purge_IsIdempotent()
    {
        await SeedAsync();
        Assert.Equal(0, await VoiceTranscriptPurge.RunAsync(_testDb.Db));
        Assert.Equal(0, await VoiceTranscriptPurge.RunAsync(_testDb.Db));
    }
}
