using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Tests.Server;

// Regression tests for the channel-recycling bug: after a channel is deleted, a newly created
// channel must NEVER reuse the freed id, so it can't inherit the old channel's content.
// Guaranteed by a persistent monotonic counter (ServersController.NextChannelIdAsync).
public class ChannelDeletionTests : IDisposable
{
    private readonly TestDb _db;
    private readonly ServersController _servers;
    private readonly MessagesController _messages;
    private GuildServer _server = null!;
    private User _admin = null!;

    public ChannelDeletionTests()
    {
        _db = new TestDb();
        _servers = new ServersController(_db.Db, TestHelpers.MockHub(), TestHelpers.NullWebhooks());
        _messages = new MessagesController(_db.Db, TestHelpers.MockHub(), new FatGuysSpeak.Server.Services.ServerMetricsService(),
            TestHelpers.NullBot(), TestHelpers.NullAutomod(), TestHelpers.NullWebhooks());
    }

    public void Dispose() => _db.Dispose();

    private async Task SeedAsync()
    {
        (_server, _admin) = await TestHelpers.SeedServerAsync(_db.Db, "owner"); // owner is Admin
        TestHelpers.SetUser(_servers, _admin.Id, _admin.Username);
        TestHelpers.SetUser(_messages, _admin.Id, _admin.Username);
    }

    private async Task<Channel> CreateChannelAsync(string name)
    {
        var result = await _servers.CreateChannel(_server.Id, new CreateChannelRequest(name, ChannelType.Text));
        var dto = (ChannelDto)((OkObjectResult)result.Result!).Value!;
        return (await _db.Db.Channels.FindAsync(dto.Id))!;
    }

    private async Task AddMessageAsync(int channelId, string content)
    {
        _db.Db.Messages.Add(new Message { Content = content, AuthorId = _admin.Id, ChannelId = channelId });
        await _db.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task CreateAfterDelete_DoesNotReuseId_AndNewChannelIsEmpty()
    {
        await SeedAsync();
        var a = await CreateChannelAsync("temp-a");
        await AddMessageAsync(a.Id, "text that belonged to temp-a");
        var oldId = a.Id;

        var del = await _servers.DeleteChannel(_server.Id, a.Id);
        Assert.IsType<NoContentResult>(del);

        var b = await CreateChannelAsync("temp-b");

        Assert.NotEqual(oldId, b.Id);                              // id was NOT reused
        Assert.True(b.Id > oldId);                                 // strictly higher
        Assert.Empty((await _messages.GetMessages(b.Id)).Value!);  // new channel shows nothing
    }

    [Fact]
    public async Task ManyCreateDeleteCycles_NeverReuseAnId()
    {
        await SeedAsync();
        var seen = new HashSet<int>(_db.Db.Channels.Select(c => c.Id));
        for (var i = 0; i < 10; i++)
        {
            var ch = await CreateChannelAsync($"c{i}");
            Assert.True(seen.Add(ch.Id), $"channel id {ch.Id} was reused on cycle {i}");
            await AddMessageAsync(ch.Id, $"msg in c{i}");
            await _servers.DeleteChannel(_server.Id, ch.Id);
        }
    }

    [Fact]
    public async Task NextChannelId_AlwaysIncreases_EvenAfterDelete()
    {
        await SeedAsync();
        var a = await CreateChannelAsync("temp");
        await _servers.DeleteChannel(_server.Id, a.Id);

        var next = await ServersController.NextChannelIdAsync(_db.Db);
        Assert.True(next > a.Id, $"next id {next} should be greater than deleted id {a.Id}");
    }

    [Fact]
    public async Task NextChannelId_CatchesUp_WhenCounterTrailsTableMax()
    {
        // Reproduces the live UNIQUE-constraint collision: another insert path (default channels
        // at server creation used auto-increment) advanced the Channels table past the persisted
        // counter, so the next explicit-id insert collided. NextChannelIdAsync must self-heal.
        await SeedAsync();
        var hi = await _db.Db.Channels.MaxAsync(c => c.Id);

        var seq = await _db.Db.AppSequences.FindAsync("channel");
        if (seq is null) { seq = new AppSequence { Name = "channel" }; _db.Db.AppSequences.Add(seq); }
        seq.Value = hi - 1;               // counter is BEHIND the real table max
        await _db.Db.SaveChangesAsync();

        var next = await ServersController.NextChannelIdAsync(_db.Db);

        Assert.True(next > hi, $"next id {next} must exceed current max {hi} to avoid a UNIQUE collision");
        Assert.False(await _db.Db.Channels.AnyAsync(c => c.Id == next), "next id must not already exist");
    }

    [Fact]
    public async Task CreateServerThenChannel_NeverCollides()
    {
        // End-to-end version of the same bug: create a fresh server (seeds default channels) and
        // then a channel — the explicit id must not collide with an auto-incremented default id.
        await SeedAsync();
        var result = await _servers.CreateServer(new CreateServerRequest("Fresh", null));
        var dto = (ServerDto)((OkObjectResult)result.Result!).Value!;

        var created = await _servers.CreateChannel(dto.Id, new CreateChannelRequest("extra", ChannelType.Text));
        Assert.IsType<OkObjectResult>(created.Result); // would be a 500 on collision
    }

    [Fact]
    public async Task NormalCreate_KeepsChannelsIsolated()
    {
        await SeedAsync();
        var a = await CreateChannelAsync("a");
        var b = await CreateChannelAsync("b");
        await AddMessageAsync(a.Id, "in a");
        await AddMessageAsync(b.Id, "in b only");

        Assert.True(b.Id > a.Id);
        var aMsgs = (await _messages.GetMessages(a.Id)).Value!;
        var bMsgs = (await _messages.GetMessages(b.Id)).Value!;
        Assert.Single(aMsgs);
        Assert.Single(bMsgs);
        Assert.Equal("in a", aMsgs[0].Content);
        Assert.Equal("in b only", bMsgs[0].Content);
    }

    [Fact]
    public async Task DeleteChannel_DefaultChannel_IsBlocked()
    {
        await SeedAsync();
        var def = await CreateChannelAsync("home");
        def.IsDefault = true;
        await _db.Db.SaveChangesAsync();

        var result = await _servers.DeleteChannel(_server.Id, def.Id);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.True(await _db.Db.Channels.AnyAsync(c => c.Id == def.Id)); // still there
    }

    [Fact]
    public async Task CreateServer_SeedsUndeletableDefaultLobby()
    {
        await SeedAsync();
        var result = await _servers.CreateServer(new CreateServerRequest("New Server", null));
        var dto = (ServerDto)((OkObjectResult)result.Result!).Value!;

        var lobby = await _db.Db.Channels.FirstOrDefaultAsync(c => c.ServerId == dto.Id && c.IsDefault);
        Assert.NotNull(lobby);
        Assert.Equal("lobby", lobby!.Name);

        var del = await _servers.DeleteChannel(dto.Id, lobby.Id);
        Assert.IsType<BadRequestObjectResult>(del);
        Assert.True(await _db.Db.Channels.AnyAsync(c => c.Id == lobby.Id));
    }

    [Fact]
    public async Task DeleteChannel_NonAdmin_Forbidden()
    {
        await SeedAsync();
        var temp = await CreateChannelAsync("temp");
        var member = new User { Username = "member", Email = "member@test.com", PasswordHash = "*" };
        _db.Db.Users.Add(member);
        await _db.Db.SaveChangesAsync();
        _db.Db.ServerMembers.Add(new ServerMember { ServerId = _server.Id, UserId = member.Id, Role = ServerRole.Member });
        await _db.Db.SaveChangesAsync();
        TestHelpers.SetUser(_servers, member.Id, member.Username);

        var result = await _servers.DeleteChannel(_server.Id, temp.Id);

        Assert.IsType<ForbidResult>(result);
        Assert.True(await _db.Db.Channels.AnyAsync(c => c.Id == temp.Id)); // still there
    }
}
