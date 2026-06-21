using System.Net;
using System.Text.Json;
using FatGuysSpeak.Server.Data;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Server.Services;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace FatGuysSpeak.Tests.Server;

public class RecapPregenServiceTests : IDisposable
{
    private readonly TestDb _db;

    public RecapPregenServiceTests() => _db = new TestDb();
    public void Dispose() => _db.Dispose();

    private static IConfiguration MakeConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Anthropic:ApiKey"] = "test-key",
                ["Anthropic:Model"]  = "claude-haiku-4-5-20251001",
            })
            .Build();

    private static IHttpClientFactory MakeHttpFactory(string replyText)
    {
        var payload = JsonSerializer.Serialize(new { content = new[] { new { type = "text", text = replyText } } });
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content    = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
            });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("anthropic"))
               .Returns(new HttpClient(handler.Object) { BaseAddress = new Uri("https://api.anthropic.com/v1/") });
        return factory.Object;
    }

    private (RecapPregenService svc, BotService bot) MakeService()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(opt => opt.UseSqlite(_db.Db.Database.GetDbConnection()));
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var bot = new BotService(MakeHttpFactory("recap"), MakeConfig(), scopeFactory, TestHelpers.MockHub(), TestHelpers.NullTts());
        var svc = new RecapPregenService(scopeFactory, bot, NullLogger<RecapPregenService>.Instance);
        return (svc, bot);
    }

    [Fact]
    public async Task RunOnce_PregeneratesTextAndVoiceForCompletedDay()
    {
        var (server, owner) = await TestHelpers.SeedServerAsync(_db.Db);
        var channel = _db.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);
        var day = DateTime.UtcNow.Date.AddDays(-1);
        _db.Db.Messages.AddRange(
            new Message { Content = "typed",  AuthorId = owner.Id, ChannelId = channel.Id, Source = MessageSource.Text,  CreatedAt = day.AddHours(8) },
            new Message { Content = "spoken", AuthorId = owner.Id, ChannelId = channel.Id, Source = MessageSource.Voice, CreatedAt = day.AddHours(9) }
        );
        await _db.Db.SaveChangesAsync();

        var (svc, _) = MakeService();
        await svc.RunOnceAsync();

        Assert.True(await _db.Db.DailyChatSummaries.AnyAsync(s => s.ChannelId == channel.Id && s.Date == day && s.Source == MessageSource.Text));
        Assert.True(await _db.Db.DailyChatSummaries.AnyAsync(s => s.ChannelId == channel.Id && s.Date == day && s.Source == MessageSource.Voice));
    }

    [Fact]
    public async Task RunOnce_IsIdempotent_DoesNotDuplicate()
    {
        var (server, owner) = await TestHelpers.SeedServerAsync(_db.Db);
        var channel = _db.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);
        var day = DateTime.UtcNow.Date.AddDays(-1);
        _db.Db.Messages.Add(new Message { Content = "typed", AuthorId = owner.Id, ChannelId = channel.Id, Source = MessageSource.Text, CreatedAt = day.AddHours(8) });
        await _db.Db.SaveChangesAsync();

        var (svc, _) = MakeService();
        await svc.RunOnceAsync();
        await svc.RunOnceAsync();   // second pass must not add another row

        Assert.Equal(1, await _db.Db.DailyChatSummaries.CountAsync(s => s.ChannelId == channel.Id && s.Date == day && s.Source == MessageSource.Text));
    }

    [Fact]
    public async Task RunOnce_DoesNotPregenerateTodaysMessages()
    {
        var (server, owner) = await TestHelpers.SeedServerAsync(_db.Db);
        var channel = _db.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);
        _db.Db.Messages.Add(new Message { Content = "today", AuthorId = owner.Id, ChannelId = channel.Id, Source = MessageSource.Text, CreatedAt = DateTime.UtcNow });
        await _db.Db.SaveChangesAsync();

        var (svc, _) = MakeService();
        await svc.RunOnceAsync();

        Assert.False(await _db.Db.DailyChatSummaries.AnyAsync(s => s.ChannelId == channel.Id));
    }
}
