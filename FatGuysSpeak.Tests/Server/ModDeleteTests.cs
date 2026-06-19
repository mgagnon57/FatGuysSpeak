using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Server.Services;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;

namespace FatGuysSpeak.Tests.Server;

// Tests for Phase 1: moderator/admin can delete other users' messages
public class ModDeleteTests : IDisposable
{
    private readonly TestDb _db;
    private readonly MessagesController _ctrl;
    private GuildServer _server = null!;
    private User _owner = null!;
    private User _member = null!;
    private User _moderator = null!;
    private Channel _channel = null!;

    public ModDeleteTests()
    {
        _db = new TestDb();
        _ctrl = new MessagesController(_db.Db, TestHelpers.MockHub(), new ServerMetricsService(), TestHelpers.NullBot(), TestHelpers.NullAutomod(), TestHelpers.NullWebhooks());
    }

    public void Dispose() => _db.Dispose();

    private async Task SeedAsync()
    {
        (_server, _owner) = await TestHelpers.SeedServerAsync(_db.Db, "owner");
        _channel = _db.Db.Channels.First(c => c.ServerId == _server.Id && c.Type == ChannelType.Text);

        _member = new User { Username = "member", Email = "member@test.com", PasswordHash = "*" };
        _moderator = new User { Username = "mod", Email = "mod@test.com", PasswordHash = "*" };
        _db.Db.Users.AddRange(_member, _moderator);
        await _db.Db.SaveChangesAsync();

        _db.Db.ServerMembers.AddRange(
            new ServerMember { ServerId = _server.Id, UserId = _member.Id, Role = ServerRole.Member },
            new ServerMember { ServerId = _server.Id, UserId = _moderator.Id, Role = ServerRole.Member }
        );
        await _db.Db.SaveChangesAsync();
    }

    private async Task<Message> AddMessageAsync(int authorId, string content = "hello")
    {
        var msg = new Message { Content = content, AuthorId = authorId, ChannelId = _channel.Id };
        _db.Db.Messages.Add(msg);
        await _db.Db.SaveChangesAsync();
        return msg;
    }

    [Fact]
    public async Task DeleteMessage_Admin_CanDeleteOthersMessage()
    {
        await SeedAsync();
        var msg = await AddMessageAsync(_member.Id, "bad message");
        TestHelpers.SetUser(_ctrl, _owner.Id, _owner.Username);

        var result = await _ctrl.DeleteMessage(_channel.Id, msg.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.True(_db.Db.Messages.Find(msg.Id)!.IsDeleted);
    }

    [Fact]
    public async Task DeleteMessage_Member_CannotDeleteOthersMessage()
    {
        await SeedAsync();
        var msg = await AddMessageAsync(_owner.Id, "owner message");
        TestHelpers.SetUser(_ctrl, _member.Id, _member.Username);

        var result = await _ctrl.DeleteMessage(_channel.Id, msg.Id);

        Assert.IsType<ForbidResult>(result);
        Assert.False(_db.Db.Messages.Find(msg.Id)!.IsDeleted);
    }

    [Fact]
    public async Task DeleteMessage_Member_CanStillDeleteOwnMessage()
    {
        await SeedAsync();
        var msg = await AddMessageAsync(_member.Id, "my own message");
        TestHelpers.SetUser(_ctrl, _member.Id, _member.Username);

        var result = await _ctrl.DeleteMessage(_channel.Id, msg.Id);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DeleteMessage_AdminDelete_WritesAuditLog()
    {
        await SeedAsync();
        var msg = await AddMessageAsync(_member.Id, "flagged content here");
        TestHelpers.SetUser(_ctrl, _owner.Id, _owner.Username);

        await _ctrl.DeleteMessage(_channel.Id, msg.Id);

        var log = _db.Db.AuditLogs.FirstOrDefault(a => a.Action == "MessageDeleted");
        Assert.NotNull(log);
        Assert.Equal(_server.Id, log.ServerId);
        Assert.Equal(_owner.Id, log.ActorId);
        Assert.Equal(_member.Id, log.TargetId);
    }

    [Fact]
    public async Task DeleteMessage_OwnMessage_NoAuditLog()
    {
        await SeedAsync();
        var msg = await AddMessageAsync(_member.Id, "my message");
        TestHelpers.SetUser(_ctrl, _member.Id, _member.Username);

        await _ctrl.DeleteMessage(_channel.Id, msg.Id);

        Assert.Empty(_db.Db.AuditLogs.Where(a => a.Action == "MessageDeleted").ToList());
    }
}
