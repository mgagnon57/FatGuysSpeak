using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Server.Services;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Tests.Server;

public class PrivateModeTests : IDisposable
{
    private readonly TestDb _testDb;
    public PrivateModeTests() => _testDb = new TestDb();
    public void Dispose() => _testDb.Dispose();

    private UsersController Users(int userId)
    {
        var c = new UsersController(_testDb.Db);
        TestHelpers.SetUser(c, userId);
        return c;
    }

    private MessagesController Messages(int userId)
    {
        var c = new MessagesController(_testDb.Db, TestHelpers.MockHub(), new ServerMetricsService(),
            TestHelpers.NullBot(), TestHelpers.NullAutomod(), TestHelpers.NullWebhooks());
        TestHelpers.SetUser(c, userId);
        return c;
    }

    private async Task<(int channelId, User user)> SeedUserWithChatAsync(string name)
    {
        var (server, user) = await TestHelpers.SeedServerAsync(_testDb.Db, name);
        var channel = _testDb.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);
        _testDb.Db.Messages.AddRange(
            new Message { Content = "typed thing", AuthorId = user.Id, ChannelId = channel.Id, Source = MessageSource.Text },
            new Message { Content = "spoken thing", AuthorId = user.Id, ChannelId = channel.Id, Source = MessageSource.Voice });
        _testDb.Db.UserAliases.Add(new UserAlias { UserId = user.Id, Alias = "Tubby" });
        await _testDb.Db.SaveChangesAsync();
        return (channel.Id, user);
    }

    [Fact]
    public async Task EnablingPrivateMode_PurgesVoiceTranscriptsAndAliases_ButKeepsText()
    {
        var (_, user) = await SeedUserWithChatAsync("alice");

        var result = await Users(user.Id).SetPrivateMode(new SetPrivateModeRequest(true));
        Assert.IsType<NoContentResult>(result);

        var fresh = await _testDb.Db.Users.FindAsync(user.Id);
        Assert.True(fresh!.PrivateMode);
        Assert.False(await _testDb.Db.Messages.AnyAsync(m => m.AuthorId == user.Id && m.Source == MessageSource.Voice));
        Assert.True(await _testDb.Db.Messages.AnyAsync(m => m.AuthorId == user.Id && m.Source == MessageSource.Text));
        Assert.False(await _testDb.Db.UserAliases.AnyAsync(a => a.UserId == user.Id));
    }

    [Fact]
    public async Task EnablingPrivateMode_OnlyAffectsTheCallingUser()
    {
        var (_, alice) = await SeedUserWithChatAsync("alice");
        var (_, bob)   = await SeedUserWithChatAsync("bob");

        await Users(alice.Id).SetPrivateMode(new SetPrivateModeRequest(true));

        // Bob is untouched: flag still off, his voice transcript and alias remain.
        var bobFresh = await _testDb.Db.Users.FindAsync(bob.Id);
        Assert.False(bobFresh!.PrivateMode);
        Assert.True(await _testDb.Db.Messages.AnyAsync(m => m.AuthorId == bob.Id && m.Source == MessageSource.Voice));
        Assert.True(await _testDb.Db.UserAliases.AnyAsync(a => a.UserId == bob.Id));
    }

    [Fact]
    public async Task PrivateUser_VoiceMessage_IsDroppedAtIngestion()
    {
        var (channelId, user) = await SeedUserWithChatAsync("alice");
        await Users(user.Id).SetPrivateMode(new SetPrivateModeRequest(true));
        var before = await _testDb.Db.Messages.CountAsync(m => m.ChannelId == channelId && m.Source == MessageSource.Voice);

        var result = await Messages(user.Id).SendMessage(channelId,
            new SendMessageRequest("nobody should log this", MessageSource.Voice));

        Assert.IsType<NoContentResult>(result.Result);
        var after = await _testDb.Db.Messages.CountAsync(m => m.ChannelId == channelId && m.Source == MessageSource.Voice);
        Assert.Equal(before, after);   // nothing new was stored
    }

    [Fact]
    public async Task NonPrivateUser_VoiceMessage_IsStored()
    {
        var (channelId, user) = await SeedUserWithChatAsync("alice");   // PrivateMode stays false

        await Messages(user.Id).SendMessage(channelId,
            new SendMessageRequest("this one is fine to log", MessageSource.Voice));

        Assert.True(await _testDb.Db.Messages.AnyAsync(m =>
            m.ChannelId == channelId && m.Source == MessageSource.Voice && m.Content == "this one is fine to log"));
    }

    [Fact]
    public async Task EphemeralAsk_StoresNothing()
    {
        var (_, user) = await TestHelpers.SeedServerAsync(_testDb.Db, "alice");
        var before = await _testDb.Db.Messages.CountAsync();

        var controller = new PorkChopController(TestHelpers.NullBot());
        TestHelpers.SetUser(controller, user.Id);

        var result = await controller.Ask(new PorkChopAskRequest("how do I cook a ribeye?"));

        Assert.NotNull(result.Value);
        Assert.False(string.IsNullOrWhiteSpace(result.Value!.Answer));
        Assert.Equal(before, await _testDb.Db.Messages.CountAsync());   // ephemeral — nothing written
    }
}
