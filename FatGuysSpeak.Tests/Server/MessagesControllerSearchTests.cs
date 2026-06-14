using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;

namespace FatGuysSpeak.Tests.Server;

public class MessagesControllerSearchTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly MessagesController _controller;
    private GuildServer _server = null!;
    private Channel _textChannel = null!;
    private User _user = null!;

    public MessagesControllerSearchTests()
    {
        _testDb = new TestDb();
        _controller = new MessagesController(
            _testDb.Db,
            TestHelpers.MockHub(),
            new FatGuysSpeak.Server.Services.ServerMetricsService(),
            TestHelpers.NullBot());
    }

    public void Dispose() => _testDb.Dispose();

    private async Task SeedAsync()
    {
        (_server, _user) = await TestHelpers.SeedServerAsync(_testDb.Db);
        _textChannel = _testDb.Db.Channels.First(c => c.ServerId == _server.Id && c.Type == ChannelType.Text);
        TestHelpers.SetUser(_controller, _user.Id, _user.Username);

        _testDb.Db.Messages.AddRange(
            new Message { Content = "hello world",      AuthorId = _user.Id, ChannelId = _textChannel.Id },
            new Message { Content = "goodbye world",    AuthorId = _user.Id, ChannelId = _textChannel.Id },
            new Message { Content = "testing search",   AuthorId = _user.Id, ChannelId = _textChannel.Id },
            new Message { Content = "another message",  AuthorId = _user.Id, ChannelId = _textChannel.Id }
        );
        await _testDb.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task Search_MatchesByContent()
    {
        await SeedAsync();

        var result = await _controller.SearchMessages(_textChannel.Id, "world");

        var list = result.Value!;
        Assert.Equal(2, list.Count);
        Assert.All(list, m => Assert.Contains("world", m.Content));
    }

    [Fact]
    public async Task Search_CaseInsensitive()
    {
        await SeedAsync();

        var result = await _controller.SearchMessages(_textChannel.Id, "HELLO");

        var list = result.Value!;
        Assert.Single(list);
    }

    [Fact]
    public async Task Search_NoMatch_ReturnsEmpty()
    {
        await SeedAsync();

        var result = await _controller.SearchMessages(_textChannel.Id, "zzznomatch");

        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task Search_ShortQuery_ReturnsBadRequest()
    {
        await SeedAsync();

        var result = await _controller.SearchMessages(_textChannel.Id, "x");

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Search_ExcludesDeletedMessages()
    {
        await SeedAsync();
        var deleted = new Message
        {
            Content = "deleted hello message",
            AuthorId = _user.Id,
            ChannelId = _textChannel.Id,
            IsDeleted = true
        };
        _testDb.Db.Messages.Add(deleted);
        await _testDb.Db.SaveChangesAsync();

        var result = await _controller.SearchMessages(_textChannel.Id, "deleted");

        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task Search_NonMember_ReturnsForbid()
    {
        await SeedAsync();
        var outsider = new User { Username = "out", Email = "out@t.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(outsider);
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_controller, outsider.Id, outsider.Username);

        var result = await _controller.SearchMessages(_textChannel.Id, "hello");

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Search_RespectsLimit()
    {
        await SeedAsync();
        for (var i = 0; i < 30; i++)
            _testDb.Db.Messages.Add(new Message
            {
                Content = $"bulk message {i}",
                AuthorId = _user.Id,
                ChannelId = _textChannel.Id
            });
        await _testDb.Db.SaveChangesAsync();

        var result = await _controller.SearchMessages(_textChannel.Id, "bulk", limit: 10);

        Assert.Equal(10, result.Value!.Count);
    }
}
