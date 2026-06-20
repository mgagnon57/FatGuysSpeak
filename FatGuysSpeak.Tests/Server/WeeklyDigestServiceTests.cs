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

[Collection("BotState")]
public class WeeklyDigestServiceTests : IDisposable
{
    private readonly TestDb _db;

    public WeeklyDigestServiceTests() => _db = new TestDb();
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

    private WeeklyDigestService MakeService()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(opt => opt.UseSqlite(_db.Db.Database.GetDbConnection()));
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var bot = new BotService(MakeHttpFactory("Weekly recap."), MakeConfig(), scopeFactory, TestHelpers.MockHub());
        return new WeeklyDigestService(scopeFactory, bot, NullLogger<WeeklyDigestService>.Instance);
    }

    [Theory]
    [InlineData("2026-06-15", "2026-06-15")]   // Monday -> itself
    [InlineData("2026-06-17", "2026-06-15")]   // Wednesday -> that Monday
    [InlineData("2026-06-21", "2026-06-15")]   // Sunday -> the prior Monday
    public void MondayOf_ReturnsWeekStart(string input, string expected)
    {
        var got = WeeklyDigestService.MondayOf(DateTime.Parse(input));
        Assert.Equal(DateTime.Parse(expected).Date, got);
    }

    [Fact]
    public async Task RunOnce_PostsDigestForLastCompletedWeek()
    {
        var (server, owner) = await TestHelpers.SeedServerAsync(_db.Db);
        var channel = _db.Db.Channels.First(c => c.ServerId == server.Id && c.Type == ChannelType.Text);

        var botUser = new User { Username = BotService.BotUsername, Email = "bot@system.local", PasswordHash = "!" };
        _db.Db.Users.Add(botUser);
        await _db.Db.SaveChangesAsync();
        BotService.BotUserId = botUser.Id;

        var weekStart = WeeklyDigestService.MondayOf(DateTime.UtcNow).AddDays(-7);
        _db.Db.Messages.Add(new Message { Content = "last week stuff", AuthorId = owner.Id, ChannelId = channel.Id, CreatedAt = weekStart.AddDays(1).AddHours(12) });
        await _db.Db.SaveChangesAsync();

        var svc = MakeService();
        await svc.RunOnceAsync();

        Assert.True(await _db.Db.WeeklyDigests.AnyAsync(w => w.ServerId == server.Id && w.WeekStart == weekStart.Date));
        Assert.True(_db.Db.Messages.Any(m => m.Source == MessageSource.AI && m.Content.Contains("Weekly digest")));
    }
}
