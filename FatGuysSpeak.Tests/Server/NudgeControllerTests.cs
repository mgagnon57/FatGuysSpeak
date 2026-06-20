using System.Net;
using System.Text.Json;
using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Server.Services;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

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

    [Fact]
    public async Task Nudge_RunsAliasLearningFirst()
    {
        var (server, owner) = await TestHelpers.SeedServerAsync(_db.Db);
        var channel = _db.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);
        var op = new User { Username = "Operator", Email = "op@test.local", PasswordHash = "!" };
        _db.Db.Users.Add(op);
        await _db.Db.SaveChangesAsync();
        _db.Db.ServerMembers.Add(new ServerMember { ServerId = server.Id, UserId = op.Id, Role = ServerRole.Member });
        for (var i = 0; i < 12; i++)
            _db.Db.Messages.Add(new Message { Content = $"medic you up {i}", AuthorId = owner.Id, ChannelId = channel.Id, Source = MessageSource.Text });
        await _db.Db.SaveChangesAsync();

        var (bot, tts) = MakeBotAndTts("{\"users\":[{\"username\":\"Operator\",\"aliases\":[\"Medic\"]}]}");
        var controller = new NudgeController(_db.Db, bot, tts);
        TestHelpers.SetUser(controller, owner.Id);

        await controller.Nudge(channel.Id);

        // The trigger ran a fresh alias-learning pass, so the inferred alias is now stored.
        Assert.True(_db.Db.UserAliases.Any(a => a.UserId == op.Id && a.Alias == "Medic"));
    }

    private (BotService bot, TtsService tts) MakeBotAndTts(string claudeReply)
    {
        var payload = JsonSerializer.Serialize(new { content = new[] { new { type = "text", text = claudeReply } } });
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content    = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
            });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>()))
               .Returns(() => new HttpClient(handler.Object) { BaseAddress = new Uri("https://api.anthropic.com/v1/") });

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Anthropic:ApiKey"] = "test-key" })
            .Build();

        var services = new ServiceCollection();
        services.AddDbContext<FatGuysSpeak.Server.Data.AppDbContext>(o => o.UseSqlite(_db.Db.Database.GetDbConnection()));
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var bot = new BotService(factory.Object, config, scopeFactory, TestHelpers.MockHub());
        var tts = new TtsService(factory.Object, new ConfigurationBuilder().Build(), TestHelpers.MockHub(), NullLogger<TtsService>.Instance);
        return (bot, tts);
    }
}
