using Squelch.Server.Models;
using Squelch.Server.Services;
using Squelch.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Squelch.Tests.Server;

public class LegacyHostRewriteTests : IDisposable
{
    private readonly TestDb _testDb = new();
    public void Dispose() => _testDb.Dispose();

    [Fact]
    public void Rewrite_Https() =>
        Assert.Equal("https://squelch-server.azurewebsites.net/uploads/a.png",
            LegacyHostRewrite.Rewrite("https://fatguysspeak-server.azurewebsites.net/uploads/a.png"));

    [Fact]
    public void Rewrite_Http_SchemePreserved() =>
        Assert.Equal("http://squelch-server.azurewebsites.net/uploads/a.png",
            LegacyHostRewrite.Rewrite("http://fatguysspeak-server.azurewebsites.net/uploads/a.png"));

    [Fact]
    public void Rewrite_CaseInsensitiveHost_AndBareOrigin()
    {
        Assert.Equal("https://squelch-server.azurewebsites.net",
            LegacyHostRewrite.Rewrite("https://FatGuysSpeak-Server.azurewebsites.net"));
    }

    [Fact]
    public void Rewrite_OtherHostAndNull_Unchanged()
    {
        Assert.Equal("http://localhost:5238/uploads/a.png", LegacyHostRewrite.Rewrite("http://localhost:5238/uploads/a.png"));
        Assert.Null(LegacyHostRewrite.Rewrite(null));
    }

    [Theory]
    [InlineData("https://evil-fatguysspeak-server.azurewebsites.net/a.png")]
    [InlineData("https://fatguysspeak-server.azurewebsites.net.evil.com/a.png")]
    [InlineData("https://example.com/?u=https://fatguysspeak-server.azurewebsites.net/a.png")]
    public void Rewrite_LookalikeOrEmbeddedHost_Unchanged(string url) =>
        Assert.Equal(url, LegacyHostRewrite.Rewrite(url));

    [Fact]
    public async Task RunAsync_RewritesAvatarAndAttachments_NotText()
    {
        var db = _testDb.Db;
        var (server, user) = await TestHelpers.SeedServerAsync(db);
        var other = new User { Username = "other", Email = "o@test.com", PasswordHash = "*" };
        db.Users.Add(other);
        await db.SaveChangesAsync();
        var old = "https://fatguysspeak-server.azurewebsites.net/uploads/a.png";
        user.AvatarUrl = old;
        var channelId = await db.Channels.Select(c => c.Id).FirstAsync();
        db.Messages.Add(new Message { AuthorId = user.Id, ChannelId = channelId, Content = old, AttachmentUrl = old });
        var conv = new DirectConversation { User1Id = Math.Min(user.Id, other.Id), User2Id = Math.Max(user.Id, other.Id) };
        db.DirectConversations.Add(conv);
        await db.SaveChangesAsync();
        db.DirectMessages.Add(new DirectMessage { ConversationId = conv.Id, AuthorId = user.Id, AttachmentUrl = old });
        await db.SaveChangesAsync();

        var changed = await LegacyHostRewrite.RunAsync(db);

        db.ChangeTracker.Clear();
        const string expected = "https://squelch-server.azurewebsites.net/uploads/a.png";
        Assert.Equal(3, changed);
        Assert.Equal(expected, (await db.Users.FindAsync(user.Id))!.AvatarUrl);
        var msg = await db.Messages.SingleAsync();
        Assert.Equal(expected, msg.AttachmentUrl);
        Assert.Equal(old, msg.Content);
        Assert.Equal(expected, (await db.DirectMessages.SingleAsync()).AttachmentUrl);
    }

    [Fact]
    public async Task RunAsync_RenamesSeededServer()
    {
        var db = _testDb.Db;
        db.Servers.AddRange(new GuildServer { Name = "FatGuysSpeak", OwnerId = 0 },
                            new GuildServer { Name = "Other", OwnerId = 0 });
        await db.SaveChangesAsync();

        var changed = await LegacyHostRewrite.RunAsync(db);

        db.ChangeTracker.Clear();
        Assert.Equal(1, changed);
        Assert.Equal(new[] { "Other", "Squelch" }, db.Servers.Select(s => s.Name).OrderBy(n => n).ToArray());
    }

    [Fact]
    public async Task RunAsync_IsIdempotent()
    {
        var db = _testDb.Db;
        var (_, user) = await TestHelpers.SeedServerAsync(db);
        user.AvatarUrl = "http://fatguysspeak-server.azurewebsites.net/x.png";
        db.Servers.Add(new GuildServer { Name = "FatGuysSpeak", OwnerId = 0 });
        await db.SaveChangesAsync();

        Assert.Equal(2, await LegacyHostRewrite.RunAsync(db));
        Assert.Equal(0, await LegacyHostRewrite.RunAsync(db));
    }
}
