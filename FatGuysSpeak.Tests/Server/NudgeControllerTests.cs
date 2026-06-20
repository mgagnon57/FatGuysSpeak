using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;

namespace FatGuysSpeak.Tests.Server;

// The on-demand nudge endpoint is admin-only; bot/tts aren't exercised on the gated paths,
// so the no-op NullBot and a null tts are enough to verify the security gate.
public class NudgeControllerTests : IDisposable
{
    private readonly TestDb _db;
    public NudgeControllerTests() => _db = new TestDb();
    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Nudge_NonMember_ReturnsForbid()
    {
        var (server, _) = await TestHelpers.SeedServerAsync(_db.Db);
        var channel = _db.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);
        var stranger = new User { Username = "stranger", Email = "s@test.local", PasswordHash = "*" };
        _db.Db.Users.Add(stranger);
        await _db.Db.SaveChangesAsync();

        var controller = new NudgeController(_db.Db, TestHelpers.NullBot(), null!);
        TestHelpers.SetUser(controller, stranger.Id);

        Assert.IsType<ForbidResult>(await controller.Nudge(channel.Id));
    }

    [Fact]
    public async Task Nudge_UnknownChannel_ReturnsNotFound()
    {
        var (_, owner) = await TestHelpers.SeedServerAsync(_db.Db);
        var controller = new NudgeController(_db.Db, TestHelpers.NullBot(), null!);
        TestHelpers.SetUser(controller, owner.Id);

        Assert.IsType<NotFoundResult>(await controller.Nudge(99999));
    }

    [Fact]
    public async Task Nudge_AdminWithBotUnconfigured_ReturnsOk()
    {
        var (server, owner) = await TestHelpers.SeedServerAsync(_db.Db);   // owner is an admin member
        var channel = _db.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);

        var controller = new NudgeController(_db.Db, TestHelpers.NullBot(), null!);
        TestHelpers.SetUser(controller, owner.Id);

        // NullBot has no key -> GenerateAndPostIdleNudgeAsync returns null -> Ok(posted:false) before any TTS.
        Assert.IsType<OkObjectResult>(await controller.Nudge(channel.Id));
    }
}
