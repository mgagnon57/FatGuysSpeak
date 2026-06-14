using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;

namespace FatGuysSpeak.Tests.Server;

public class ReactionsControllerTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly ReactionsController _controller;
    private GuildServer _server = null!;
    private Channel _channel = null!;
    private User _owner = null!;
    private User _other = null!;
    private Message _message = null!;

    public ReactionsControllerTests()
    {
        _testDb = new TestDb();
        _controller = new ReactionsController(_testDb.Db, TestHelpers.MockHub());
    }

    public void Dispose() => _testDb.Dispose();

    private async Task SeedAsync()
    {
        (_server, _owner) = await TestHelpers.SeedServerAsync(_testDb.Db, "owner");
        _other = new User { Username = "other", Email = "other@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(_other);
        await _testDb.Db.SaveChangesAsync();
        _testDb.Db.ServerMembers.Add(new ServerMember { ServerId = _server.Id, UserId = _other.Id });
        await _testDb.Db.SaveChangesAsync();

        _channel = _testDb.Db.Channels.First(c => c.ServerId == _server.Id && c.Type == ChannelType.Text);
        _message = new Message { Content = "hello", AuthorId = _owner.Id, ChannelId = _channel.Id };
        _testDb.Db.Messages.Add(_message);
        await _testDb.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task Toggle_AddReaction_ReturnsCountOne()
    {
        await SeedAsync();
        TestHelpers.SetUser(_controller, _owner.Id, _owner.Username);

        var result = await _controller.Toggle(_channel.Id, _message.Id, "👍");

        var dto = (result.Result as OkObjectResult)?.Value as ReactionsUpdatedDto;
        Assert.NotNull(dto);
        Assert.Single(dto.Reactions);
        Assert.Equal("👍", dto.Reactions[0].Emoji);
        Assert.Equal(1, dto.Reactions[0].Count);
        Assert.True(dto.Reactions[0].IsOwn);
    }

    [Fact]
    public async Task Toggle_SameEmojiTwice_RemovesReaction()
    {
        await SeedAsync();
        TestHelpers.SetUser(_controller, _owner.Id, _owner.Username);

        await _controller.Toggle(_channel.Id, _message.Id, "👍");
        var result = await _controller.Toggle(_channel.Id, _message.Id, "👍");

        var dto = (result.Result as OkObjectResult)?.Value as ReactionsUpdatedDto;
        Assert.NotNull(dto);
        Assert.Empty(dto.Reactions);
    }

    [Fact]
    public async Task Toggle_TwoUsersReact_CountIsTwo()
    {
        await SeedAsync();
        TestHelpers.SetUser(_controller, _owner.Id, _owner.Username);
        await _controller.Toggle(_channel.Id, _message.Id, "❤️");

        TestHelpers.SetUser(_controller, _other.Id, _other.Username);
        var result = await _controller.Toggle(_channel.Id, _message.Id, "❤️");

        var dto = (result.Result as OkObjectResult)?.Value as ReactionsUpdatedDto;
        Assert.NotNull(dto);
        Assert.Equal(2, dto.Reactions[0].Count);
        Assert.True(dto.Reactions[0].IsOwn); // _other is the current user here
    }

    [Fact]
    public async Task Toggle_DifferentEmoji_TwoGroupsReturned()
    {
        await SeedAsync();
        TestHelpers.SetUser(_controller, _owner.Id, _owner.Username);
        await _controller.Toggle(_channel.Id, _message.Id, "👍");

        TestHelpers.SetUser(_controller, _other.Id, _other.Username);
        var result = await _controller.Toggle(_channel.Id, _message.Id, "❤️");

        var dto = (result.Result as OkObjectResult)?.Value as ReactionsUpdatedDto;
        Assert.NotNull(dto);
        Assert.Equal(2, dto.Reactions.Count);
    }

    [Fact]
    public async Task Toggle_InvalidEmoji_ReturnsBadRequest()
    {
        await SeedAsync();
        TestHelpers.SetUser(_controller, _owner.Id, _owner.Username);

        var result = await _controller.Toggle(_channel.Id, _message.Id, "");

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Toggle_NonExistentMessage_ReturnsNotFound()
    {
        await SeedAsync();
        TestHelpers.SetUser(_controller, _owner.Id, _owner.Username);

        var result = await _controller.Toggle(_channel.Id, 999_999, "👍");

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Toggle_DeletedMessage_ReturnsBadRequest()
    {
        await SeedAsync();
        _message.IsDeleted = true;
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_controller, _owner.Id, _owner.Username);

        var result = await _controller.Toggle(_channel.Id, _message.Id, "👍");

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task BuildReactions_IsOwn_FalseForOtherUser()
    {
        await SeedAsync();
        TestHelpers.SetUser(_controller, _owner.Id, _owner.Username);
        await _controller.Toggle(_channel.Id, _message.Id, "👍");

        var reactions = await _controller.BuildReactionsAsync(_message.Id, _other.Id);

        Assert.Single(reactions);
        Assert.False(reactions[0].IsOwn); // _other hasn't reacted
    }

    [Fact]
    public async Task GetMessages_IncludesReactions()
    {
        await SeedAsync();
        TestHelpers.SetUser(_controller, _owner.Id, _owner.Username);
        await _controller.Toggle(_channel.Id, _message.Id, "🎉");

        var msgsController = new MessagesController(
            _testDb.Db, TestHelpers.MockHub(), new FatGuysSpeak.Server.Services.ServerMetricsService(), TestHelpers.NullBot());
        TestHelpers.SetUser(msgsController, _owner.Id, _owner.Username);

        var result = await msgsController.GetMessages(_channel.Id);

        var msgs = result.Value ?? (result.Result as OkObjectResult)?.Value as List<MessageDto>;
        Assert.NotNull(msgs);
        var msg = Assert.Single(msgs);
        Assert.NotNull(msg.Reactions);
        Assert.Single(msg.Reactions!);
        Assert.Equal("🎉", msg.Reactions![0].Emoji);
    }
}
