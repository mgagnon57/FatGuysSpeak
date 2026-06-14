using System.Security.Claims;
using FatGuysSpeak.Server.Data;
using FatGuysSpeak.Server.Hubs;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Server.Services;
using FatGuysSpeak.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;

namespace FatGuysSpeak.Tests.Helpers;

public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _conn;
    public AppDbContext Db { get; }

    public TestDb()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_conn)
            .Options;
        Db = new AppDbContext(opts);
        Db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        Db.Dispose();
        _conn.Dispose();
    }
}

public static class TestHelpers
{
    public static TokenService CreateTokenService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "FatGuysSpeakSuperSecretKeyForTests32Chars!",
                ["Jwt:Issuer"] = "FatGuysSpeak",
                ["Jwt:Audience"] = "FatGuysSpeak"
            })
            .Build();
        return new TokenService(config);
    }

    public static void SetUser(ControllerBase controller, int userId, string username = "testuser")
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, username)
        };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
            }
        };
    }

    public static FatGuysSpeak.Server.Services.BotService NullBot()
    {
        var config = new ConfigurationBuilder().Build(); // ApiKey empty → RespondAsync returns immediately
        var httpFactory = new Mock<IHttpClientFactory>().Object;
        var scopeFactory = new Mock<IServiceScopeFactory>().Object;
        return new FatGuysSpeak.Server.Services.BotService(httpFactory, config, scopeFactory, MockHub());
    }

    public static IHubContext<ChatHub> MockHub()
    {
        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);

        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
        clients.Setup(c => c.User(It.IsAny<string>())).Returns(proxy.Object);

        var hub = new Mock<IHubContext<ChatHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        return hub.Object;
    }

    public static async Task<(GuildServer server, User user)> SeedServerAsync(
        AppDbContext db, string username = "owner")
    {
        var user = new User { Username = username, Email = $"{username}@test.com", PasswordHash = "*" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var server = new GuildServer { Name = "FatGuysSpeak", OwnerId = user.Id };
        db.Servers.Add(server);
        await db.SaveChangesAsync();

        db.Channels.AddRange(
            new Channel { Name = "general", Type = ChannelType.Text, ServerId = server.Id, Position = 0 },
            new Channel { Name = "General Voice", Type = ChannelType.Voice, ServerId = server.Id, Position = 1 }
        );
        db.ServerMembers.Add(new ServerMember { ServerId = server.Id, UserId = user.Id, Role = ServerRole.Admin });
        await db.SaveChangesAsync();

        return (server, user);
    }
}
