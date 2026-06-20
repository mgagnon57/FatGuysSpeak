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

[Collection("BotState")]
public class BotServiceTests : IDisposable
{
    private readonly TestDb _db;

    public BotServiceTests() => _db = new TestDb();
    public void Dispose() => _db.Dispose();

    private static IConfiguration MakeConfig(string apiKey = "test-key", bool announceJoins = true) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Anthropic:ApiKey"] = apiKey,
                ["Anthropic:Model"]  = "claude-haiku-4-5-20251001",
                ["PorkChop:AnnounceJoins"] = announceJoins ? "true" : "false",
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

    private BotService MakeBotService(IHttpClientFactory httpFactory, IConfiguration config, string? contentRoot = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<FatGuysSpeak.Server.Data.AppDbContext>(opt =>
            opt.UseSqlite(_db.Db.Database.GetDbConnection()));
        if (contentRoot is not null)
            services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new FakeEnv { ContentRootPath = contentRoot });

        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        return new BotService(httpFactory, config, scopeFactory, TestHelpers.MockHub());
    }

    private sealed class FakeEnv : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string ApplicationName { get; set; } = "tests";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
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
    public async Task RespondAsync_IncludesItsOwnRepliesInContext()
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
        Assert.Contains("previous AI reply", body);   // PorkChop now remembers its own side, so follow-ups stay on topic
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
    public async Task WeeklyDigest_GeneratesRow_PostsBotMessage_AndIsIdempotent()
    {
        var (server, owner) = await TestHelpers.SeedServerAsync(_db.Db);
        var channel = _db.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);

        var botUser = new User { Username = BotService.BotUsername, Email = "bot@system.local", PasswordHash = "!" };
        _db.Db.Users.Add(botUser);
        await _db.Db.SaveChangesAsync();
        BotService.BotUserId = botUser.Id;

        var weekStart = FatGuysSpeak.Server.Services.WeeklyDigestService.MondayOf(DateTime.UtcNow).AddDays(-7);
        _db.Db.Messages.AddRange(
            new Message { Content = "monday chatter",  AuthorId = owner.Id, ChannelId = channel.Id, CreatedAt = weekStart.AddDays(0).AddHours(10) },
            new Message { Content = "wednesday plans", AuthorId = owner.Id, ChannelId = channel.Id, CreatedAt = weekStart.AddDays(2).AddHours(14) }
        );
        await _db.Db.SaveChangesAsync();

        var svc = MakeBotService(MakeHttpFactory("Big week: lots happened."), MakeConfig());
        var digest = await svc.GenerateAndPostWeeklyDigestAsync(server.Id, weekStart);

        Assert.NotNull(digest);
        Assert.Equal(2, digest!.MessageCount);
        Assert.True(await _db.Db.WeeklyDigests.AnyAsync(w => w.ServerId == server.Id && w.WeekStart == weekStart.Date));

        var posted = _db.Db.Messages.FirstOrDefault(m => m.Source == MessageSource.AI && m.AuthorId == botUser.Id);
        Assert.NotNull(posted);
        Assert.Contains("Weekly digest", posted!.Content);
        Assert.Contains("Big week", posted.Content);

        // Second pass must not post a duplicate digest.
        var again = await svc.GenerateAndPostWeeklyDigestAsync(server.Id, weekStart);
        Assert.Null(again);
        Assert.Equal(1, await _db.Db.WeeklyDigests.CountAsync(w => w.ServerId == server.Id && w.WeekStart == weekStart.Date));
    }

    [Fact]
    public async Task WeeklyDigest_QuietWeek_PostsNothing()
    {
        var (server, _) = await TestHelpers.SeedServerAsync(_db.Db);
        BotService.BotUserId = 999;
        var weekStart = FatGuysSpeak.Server.Services.WeeklyDigestService.MondayOf(DateTime.UtcNow).AddDays(-7);

        var svc = MakeBotService(MakeHttpFactory("unused"), MakeConfig());
        var digest = await svc.GenerateAndPostWeeklyDigestAsync(server.Id, weekStart);

        Assert.Null(digest);
        Assert.False(await _db.Db.WeeklyDigests.AnyAsync(w => w.ServerId == server.Id));
    }

    [Fact]
    public async Task Catchup_SummarizesWhatYouMissed_ExcludingYourOwnMessages()
    {
        var (server, owner) = await TestHelpers.SeedServerAsync(_db.Db);
        var channel = _db.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);
        var buddy = new User { Username = "buddy", Email = "buddy@test.local", PasswordHash = "!" };
        _db.Db.Users.Add(buddy);
        owner.LastSeenAt = DateTime.UtcNow.AddHours(-2);   // "last online" two hours ago
        await _db.Db.SaveChangesAsync();

        _db.Db.Messages.AddRange(
            new Message { Content = "you missed this",   AuthorId = buddy.Id, ChannelId = channel.Id, Source = MessageSource.Text,  CreatedAt = DateTime.UtcNow.AddHours(-1) },
            new Message { Content = "and this too",       AuthorId = buddy.Id, ChannelId = channel.Id, Source = MessageSource.Text,  CreatedAt = DateTime.UtcNow.AddMinutes(-30) },
            new Message { Content = "spoken, other tab",  AuthorId = buddy.Id, ChannelId = channel.Id, Source = MessageSource.Voice, CreatedAt = DateTime.UtcNow.AddMinutes(-25) },
            new Message { Content = "my own message",     AuthorId = owner.Id, ChannelId = channel.Id, Source = MessageSource.Text,  CreatedAt = DateTime.UtcNow.AddMinutes(-20) },
            new Message { Content = "old, before I left", AuthorId = buddy.Id, ChannelId = channel.Id, Source = MessageSource.Text,  CreatedAt = DateTime.UtcNow.AddHours(-5) }
        );
        await _db.Db.SaveChangesAsync();

        var svc = MakeBotService(MakeHttpFactory("Here's what you missed."), MakeConfig());
        var result = await svc.GenerateCatchupAsync(owner.Id, MessageSource.Text);

        Assert.Equal(2, result.MessageCount);   // only buddy's two TEXT messages after last-seen; own, voice, and pre-departure excluded
        Assert.Equal("Here's what you missed.", result.Summary);

        var voiceResult = await svc.GenerateCatchupAsync(owner.Id, MessageSource.Voice);
        Assert.Equal(1, voiceResult.MessageCount);   // only the spoken message
    }

    [Fact]
    public async Task RespondAsync_WithImageAttachment_SendsImageBlockToClaude()
    {
        var (server, owner) = await TestHelpers.SeedServerAsync(_db.Db);
        var channel = _db.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);

        var botUser = new User { Username = BotService.BotUsername, Email = "bot@system.local", PasswordHash = "!" };
        _db.Db.Users.Add(botUser);
        await _db.Db.SaveChangesAsync();
        BotService.BotUserId = botUser.Id;

        // Temp content root with an uploads dir + a small "image" file.
        var root = Path.Combine(Path.GetTempPath(), "fgs-vision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "uploads"));
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5 };
        File.WriteAllBytes(Path.Combine(root, "uploads", "pic.png"), pngBytes);

        _db.Db.Messages.Add(new Message
        {
            Content = "check this out", AuthorId = owner.Id, ChannelId = channel.Id,
            AttachmentUrl = "http://localhost:5238/uploads/pic.png", Source = MessageSource.Text
        });
        await _db.Db.SaveChangesAsync();

        HttpRequestMessage? captured = null;
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content    = new StringContent(
                    JsonSerializer.Serialize(new { content = new[] { new { type = "text", text = "Looks like a PNG." } } }),
                    System.Text.Encoding.UTF8, "application/json")
            });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("anthropic"))
               .Returns(new HttpClient(handler.Object) { BaseAddress = new Uri("https://api.anthropic.com/v1/") });

        var svc = MakeBotService(factory.Object, MakeConfig(), contentRoot: root);
        await svc.RespondAsync(channel.Id, server.Id, "@PorkChop what's in this image?");

        var body = await captured!.Content!.ReadAsStringAsync();
        Assert.Contains("\"type\":\"image\"", body);
        Assert.Contains("image/png", body);
        Assert.Contains(Convert.ToBase64String(pngBytes), body);

        try { Directory.Delete(root, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Catchup_NothingNew_ReturnsCaughtUp()
    {
        var (_, owner) = await TestHelpers.SeedServerAsync(_db.Db);
        owner.LastSeenAt = DateTime.UtcNow.AddMinutes(-5);
        await _db.Db.SaveChangesAsync();

        var svc = MakeBotService(MakeHttpFactory("should not be called"), MakeConfig());
        var result = await svc.GenerateCatchupAsync(owner.Id, MessageSource.Text);

        Assert.Equal(0, result.MessageCount);
        Assert.Contains("caught up", result.Summary);
    }

    // ── Join announcements ──────────────────────────────────────────────────────
    private async Task<(GuildServer server, User joiner)> SeedJoinScenarioAsync()
    {
        var (server, owner) = await TestHelpers.SeedServerAsync(_db.Db);
        var botUser = new User { Username = BotService.BotUsername, Email = "bot@system.local", PasswordHash = "!" };
        _db.Db.Users.Add(botUser);
        await _db.Db.SaveChangesAsync();
        BotService.BotUserId = botUser.Id;
        owner.Bio = "Always grinding (mostly the snack drawer)";
        await _db.Db.SaveChangesAsync();
        return (server, owner);
    }

    private bool AnyAnnouncement() =>
        _db.Db.Messages.Any(m => m.Source == MessageSource.AI && m.AuthorId == BotService.BotUserId);

    [Fact]
    public async Task AnnounceJoin_ReturningUser_PostsWelcome()
    {
        var (_, joiner) = await SeedJoinScenarioAsync();
        var svc = MakeBotService(MakeHttpFactory("Look who waddled back in — welcome back!"), MakeConfig());

        await svc.AnnounceJoinAsync(joiner.Id, awaySince: DateTime.UtcNow.AddHours(-2));

        Assert.True(AnyAnnouncement());
    }

    [Fact]
    public async Task AnnounceJoin_QuickReconnect_PostsNothing()
    {
        var (_, joiner) = await SeedJoinScenarioAsync();
        var svc = MakeBotService(MakeHttpFactory("unused"), MakeConfig());

        await svc.AnnounceJoinAsync(joiner.Id, awaySince: DateTime.UtcNow.AddMinutes(-2));   // back within cooldown

        Assert.False(AnyAnnouncement());
    }

    [Fact]
    public async Task AnnounceJoin_SecondCallWithinCooldown_PostsOnce()
    {
        var (_, joiner) = await SeedJoinScenarioAsync();
        var svc = MakeBotService(MakeHttpFactory("welcome"), MakeConfig());

        await svc.AnnounceJoinAsync(joiner.Id, awaySince: DateTime.UtcNow.AddHours(-2));
        await svc.AnnounceJoinAsync(joiner.Id, awaySince: DateTime.UtcNow.AddHours(-2));   // deduped

        Assert.Equal(1, _db.Db.Messages.Count(m => m.Source == MessageSource.AI && m.AuthorId == BotService.BotUserId));
    }

    [Fact]
    public async Task AnnounceJoin_NoApiKey_PostsNothing()
    {
        var (_, joiner) = await SeedJoinScenarioAsync();
        var svc = MakeBotService(MakeHttpFactory("unused"), MakeConfig(apiKey: ""));

        await svc.AnnounceJoinAsync(joiner.Id, awaySince: DateTime.UtcNow.AddHours(-2));

        Assert.False(AnyAnnouncement());
    }

    [Fact]
    public async Task AnnounceJoin_Disabled_PostsNothing()
    {
        var (_, joiner) = await SeedJoinScenarioAsync();
        var svc = MakeBotService(MakeHttpFactory("unused"), MakeConfig(announceJoins: false));

        await svc.AnnounceJoinAsync(joiner.Id, awaySince: DateTime.UtcNow.AddHours(-2));

        Assert.False(AnyAnnouncement());
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
