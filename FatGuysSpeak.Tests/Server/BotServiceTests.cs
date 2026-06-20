using System.Net;
using System.Text.Json;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Server.Services;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Moq.Protected;

namespace FatGuysSpeak.Tests.Server;

public class BotServiceTests : IDisposable
{
    private readonly TestDb _db;

    public BotServiceTests() => _db = new TestDb();
    public void Dispose() => _db.Dispose();

    private static IConfiguration MakeConfig(string apiKey = "test-key") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Anthropic:ApiKey"] = apiKey,
                ["Anthropic:Model"]  = "claude-haiku-4-5-20251001",
            })
            .Build();

    private static IHttpClientFactory MakeHttpFactory(string replyText)
    {
        var responsePayload = JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = replyText } }
        });

        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            // Build a fresh response per call — the content stream is consumed on read, so a
            // single shared instance would fail the second request (e.g. text then voice).
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content    = new StringContent(responsePayload, System.Text.Encoding.UTF8, "application/json")
            });

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("anthropic"))
               .Returns(new HttpClient(handler.Object) { BaseAddress = new Uri("https://api.anthropic.com/v1/") });
        return factory.Object;
    }

    private BotService MakeBotService(IHttpClientFactory httpFactory, IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddDbContext<FatGuysSpeak.Server.Data.AppDbContext>(opt =>
            opt.UseSqlite(_db.Db.Database.GetDbConnection()));

        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        return new BotService(httpFactory, config, scopeFactory, TestHelpers.MockHub());
    }

    [Fact]
    public async Task RespondAsync_SavesBotMessageWithAISource()
    {
        var (server, _) = await TestHelpers.SeedServerAsync(_db.Db);
        var channel = _db.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);

        var botUser = new User { Username = BotService.BotUsername, Email = "bot@system.local", PasswordHash = "!" };
        _db.Db.Users.Add(botUser);
        await _db.Db.SaveChangesAsync();
        BotService.BotUserId = botUser.Id;

        var svc = MakeBotService(MakeHttpFactory("Hello from PorkChop!"), MakeConfig());

        await svc.RespondAsync(channel.Id, server.Id, "@PorkChop hello");

        var msg = _db.Db.Messages.FirstOrDefault(m => m.Source == MessageSource.AI);
        Assert.NotNull(msg);
        Assert.Equal("Hello from PorkChop!", msg.Content);
        Assert.Equal(BotService.BotUserId, msg.AuthorId);
    }

    [Fact]
    public async Task RespondAsync_IncludesRecentContextInPrompt()
    {
        var (server, owner) = await TestHelpers.SeedServerAsync(_db.Db);
        var channel = _db.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);

        _db.Db.Messages.Add(new Message { Content = "existing message", AuthorId = owner.Id, ChannelId = channel.Id });
        await _db.Db.SaveChangesAsync();

        var botUser = new User { Username = BotService.BotUsername, Email = "bot@system.local", PasswordHash = "!" };
        _db.Db.Users.Add(botUser);
        await _db.Db.SaveChangesAsync();
        BotService.BotUserId = botUser.Id;

        HttpRequestMessage? captured = null;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content    = new StringContent(
                    JsonSerializer.Serialize(new { content = new[] { new { type = "text", text = "reply" } } }),
                    System.Text.Encoding.UTF8, "application/json")
            });

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("anthropic"))
               .Returns(new HttpClient(handler.Object) { BaseAddress = new Uri("https://api.anthropic.com/v1/") });

        var svc = MakeBotService(factory.Object, MakeConfig());
        await svc.RespondAsync(channel.Id, server.Id, "@PorkChop hi");

        Assert.NotNull(captured);
        var body = await captured!.Content!.ReadAsStringAsync();
        Assert.Contains("existing message", body);
    }

    [Fact]
    public async Task RespondAsync_DoesNothing_WhenApiKeyEmpty()
    {
        var (server, _) = await TestHelpers.SeedServerAsync(_db.Db);
        var channel = _db.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);
        BotService.BotUserId = 999;

        var svc = MakeBotService(MakeHttpFactory("should not be called"), MakeConfig(apiKey: ""));

        await svc.RespondAsync(channel.Id, server.Id, "@PorkChop hello");

        Assert.False(_db.Db.Messages.Any(m => m.Source == MessageSource.AI));
    }

    [Fact]
    public async Task RespondAsync_DoesNothing_WhenBotUserIdZero()
    {
        var (server, _) = await TestHelpers.SeedServerAsync(_db.Db);
        var channel = _db.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);
        BotService.BotUserId = 0;

        var svc = MakeBotService(MakeHttpFactory("should not be called"), MakeConfig());

        await svc.RespondAsync(channel.Id, server.Id, "@PorkChop hello");

        Assert.False(_db.Db.Messages.Any(m => m.Source == MessageSource.AI));
    }

    [Fact]
    public async Task RespondAsync_DoesNotIncludeAIMessagesInContext()
    {
        var (server, owner) = await TestHelpers.SeedServerAsync(_db.Db);
        var channel = _db.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);

        var botUser = new User { Username = BotService.BotUsername, Email = "bot@system.local", PasswordHash = "!" };
        _db.Db.Users.Add(botUser);
        await _db.Db.SaveChangesAsync();
        BotService.BotUserId = botUser.Id;

        _db.Db.Messages.AddRange(
            new Message { Content = "user message",     AuthorId = owner.Id,   ChannelId = channel.Id, Source = MessageSource.Text },
            new Message { Content = "previous AI reply", AuthorId = botUser.Id, ChannelId = channel.Id, Source = MessageSource.AI }
        );
        await _db.Db.SaveChangesAsync();

        HttpRequestMessage? captured = null;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content    = new StringContent(
                    JsonSerializer.Serialize(new { content = new[] { new { type = "text", text = "reply" } } }),
                    System.Text.Encoding.UTF8, "application/json")
            });

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("anthropic"))
               .Returns(new HttpClient(handler.Object) { BaseAddress = new Uri("https://api.anthropic.com/v1/") });

        var svc = MakeBotService(factory.Object, MakeConfig());
        await svc.RespondAsync(channel.Id, server.Id, "@PorkChop hi");

        var body = await captured!.Content!.ReadAsStringAsync();
        Assert.Contains("user message", body);
        Assert.DoesNotContain("previous AI reply", body);
    }

    // ── Daily summaries ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetOrCreateDailySummary_PastDayWithMessages_GeneratesAndCaches()
    {
        var (server, owner) = await TestHelpers.SeedServerAsync(_db.Db);
        var channel = _db.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);
        var day = DateTime.UtcNow.Date.AddDays(-1);
        _db.Db.Messages.Add(new Message { Content = "hey everyone", AuthorId = owner.Id, ChannelId = channel.Id, CreatedAt = day.AddHours(10) });
        await _db.Db.SaveChangesAsync();

        var svc = MakeBotService(MakeHttpFactory("Folks said hi and chatted."), MakeConfig());
        var result = await svc.GetOrCreateDailySummaryAsync(channel.Id, day, MessageSource.Text);

        Assert.NotNull(result);
        Assert.Equal("Folks said hi and chatted.", result!.Summary);
        Assert.Equal(1, result.MessageCount);
        Assert.True(await _db.Db.DailyChatSummaries.AnyAsync(s => s.ChannelId == channel.Id && s.Date == day));

        var again = await svc.GetOrCreateDailySummaryAsync(channel.Id, day, MessageSource.Text);
        Assert.Equal(result.Summary, again!.Summary);   // served from cache
    }

    [Fact]
    public async Task GetOrCreateDailySummary_TextAndVoice_AreSummarizedSeparately()
    {
        var (server, owner) = await TestHelpers.SeedServerAsync(_db.Db);
        var channel = _db.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);
        var day = DateTime.UtcNow.Date.AddDays(-1);
        _db.Db.Messages.AddRange(
            new Message { Content = "typed one",  AuthorId = owner.Id, ChannelId = channel.Id, Source = MessageSource.Text,  CreatedAt = day.AddHours(9) },
            new Message { Content = "typed two",  AuthorId = owner.Id, ChannelId = channel.Id, Source = MessageSource.Text,  CreatedAt = day.AddHours(10) },
            new Message { Content = "spoken one", AuthorId = owner.Id, ChannelId = channel.Id, Source = MessageSource.Voice, CreatedAt = day.AddHours(11) }
        );
        await _db.Db.SaveChangesAsync();

        var svc = MakeBotService(MakeHttpFactory("recap"), MakeConfig());

        var text  = await svc.GetOrCreateDailySummaryAsync(channel.Id, day, MessageSource.Text);
        var voice = await svc.GetOrCreateDailySummaryAsync(channel.Id, day, MessageSource.Voice);

        Assert.Equal(2, text!.MessageCount);    // only the two typed messages
        Assert.Equal(1, voice!.MessageCount);   // only the one spoken message

        // Two independent cached rows, one per source.
        Assert.True(await _db.Db.DailyChatSummaries.AnyAsync(s => s.ChannelId == channel.Id && s.Date == day && s.Source == MessageSource.Text));
        Assert.True(await _db.Db.DailyChatSummaries.AnyAsync(s => s.ChannelId == channel.Id && s.Date == day && s.Source == MessageSource.Voice));
    }

    [Fact]
    public async Task GetOrCreateDailySummary_Today_ReturnsNull()
    {
        var (server, _) = await TestHelpers.SeedServerAsync(_db.Db);
        var channel = _db.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);
        var svc = MakeBotService(MakeHttpFactory("x"), MakeConfig());
        Assert.Null(await svc.GetOrCreateDailySummaryAsync(channel.Id, DateTime.UtcNow.Date, MessageSource.Text));
    }

    [Fact]
    public async Task GetOrCreateDailySummary_QuietDay_ReturnsQuietWithoutApiText()
    {
        var (server, _) = await TestHelpers.SeedServerAsync(_db.Db);
        var channel = _db.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);
        var svc = MakeBotService(MakeHttpFactory("should not be used"), MakeConfig());
        var result = await svc.GetOrCreateDailySummaryAsync(channel.Id, DateTime.UtcNow.Date.AddDays(-2), MessageSource.Text);

        Assert.NotNull(result);
        Assert.Equal(0, result!.MessageCount);
        Assert.Contains("Quiet day", result.Summary);
    }
}
