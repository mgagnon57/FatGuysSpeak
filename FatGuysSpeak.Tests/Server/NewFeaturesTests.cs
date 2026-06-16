using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Server.Services;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Tests.Server;

public class AutomodServiceTests
{
    [Fact]
    public void IsSpam_BelowLimit_ReturnsFalse()
    {
        var svc = new AutomodService();
        for (int i = 0; i < 5; i++)
            Assert.False(svc.IsSpam(1, 1, $"msg {i}"));
    }

    [Fact]
    public void IsSpam_ExceedsLimit_ReturnsTrue()
    {
        var svc = new AutomodService();
        for (int i = 0; i < 5; i++)
            svc.IsSpam(1, 1, $"unique message {i}");
        Assert.True(svc.IsSpam(1, 1, "sixth unique message"));
    }

    [Fact]
    public void IsSpam_DuplicateMessage_ReturnsTrue()
    {
        var svc = new AutomodService();
        svc.IsSpam(1, 1, "hello");
        Assert.True(svc.IsSpam(1, 1, "hello"));
    }

    [Fact]
    public void IsSpam_DifferentUsers_IndependentTracking()
    {
        var svc = new AutomodService();
        for (int i = 0; i < 5; i++)
            svc.IsSpam(1, 1, $"msg {i}");
        Assert.False(svc.IsSpam(2, 1, "another user first message"));
    }
}

public class WordFilterSeverityTests : IDisposable
{
    private readonly TestDb _db;
    private readonly WordFiltersController _filterCtrl;
    private readonly MessagesController _msgCtrl;
    private GuildServer _server = null!;
    private User _admin = null!;
    private User _member = null!;

    public WordFilterSeverityTests()
    {
        _db = new TestDb();
        _filterCtrl = new WordFiltersController(_db.Db);
        _msgCtrl = new MessagesController(_db.Db, TestHelpers.MockHub(),
            new ServerMetricsService(), TestHelpers.NullBot(), TestHelpers.NullAutomod(), TestHelpers.NullWebhooks());
    }

    public void Dispose() => _db.Dispose();

    private async Task SeedAsync()
    {
        (_server, _admin) = await TestHelpers.SeedServerAsync(_db.Db);
        _member = new User { Username = "member", Email = "m@test.com", PasswordHash = "*" };
        _db.Db.Users.Add(_member);
        await _db.Db.SaveChangesAsync();
        _db.Db.ServerMembers.Add(new ServerMember { ServerId = _server.Id, UserId = _member.Id, Role = ServerRole.Member });
        await _db.Db.SaveChangesAsync();
    }

    private int ChannelId => _db.Db.Channels.First(c => c.ServerId == _server.Id && c.Type == ChannelType.Text).Id;

    [Fact]
    public async Task WordFilter_SeverityDelete_BlocksMessage()
    {
        await SeedAsync();
        _db.Db.WordFilters.Add(new WordFilter { ServerId = _server.Id, Pattern = "badword", Severity = WordFilterSeverity.Delete });
        await _db.Db.SaveChangesAsync();
        TestHelpers.SetUser(_msgCtrl, _member.Id, _member.Username);

        var result = await _msgCtrl.SendMessage(ChannelId, new SendMessageRequest("Hello badword world"));

        Assert.Equal(403, ((ObjectResult)result.Result!).StatusCode);
    }

    [Fact]
    public async Task WordFilter_SeverityMute_BlocksAndMutes()
    {
        await SeedAsync();
        _db.Db.WordFilters.Add(new WordFilter { ServerId = _server.Id, Pattern = "offensive", Severity = WordFilterSeverity.Mute });
        await _db.Db.SaveChangesAsync();
        TestHelpers.SetUser(_msgCtrl, _member.Id, _member.Username);

        var result = await _msgCtrl.SendMessage(ChannelId, new SendMessageRequest("this is offensive content"));

        Assert.Equal(403, ((ObjectResult)result.Result!).StatusCode);
        var updatedMember = await _db.Db.ServerMembers.FindAsync(_server.Id, _member.Id);
        Assert.NotNull(updatedMember!.MutedUntil);
        Assert.True(updatedMember.MutedUntil > DateTime.UtcNow);
    }

    [Fact]
    public async Task WordFilter_SeverityLog_ReplacesWordAndAllows()
    {
        await SeedAsync();
        _db.Db.WordFilters.Add(new WordFilter { ServerId = _server.Id, Pattern = "logged", Severity = WordFilterSeverity.Log });
        await _db.Db.SaveChangesAsync();
        TestHelpers.SetUser(_msgCtrl, _member.Id, _member.Username);

        var result = await _msgCtrl.SendMessage(ChannelId, new SendMessageRequest("this is logged content"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<MessageDto>(ok.Value);
        Assert.Contains("******", dto.Content);
    }

    [Fact]
    public async Task WordFilter_LeetSpeakDetection_MatchesNormalized()
    {
        await SeedAsync();
        _db.Db.WordFilters.Add(new WordFilter { ServerId = _server.Id, Pattern = "bad", Severity = WordFilterSeverity.Delete });
        await _db.Db.SaveChangesAsync();
        TestHelpers.SetUser(_msgCtrl, _member.Id, _member.Username);

        // "b4d" normalizes to "bad" via leet-speak
        var result = await _msgCtrl.SendMessage(ChannelId, new SendMessageRequest("this is b4d content"));

        Assert.Equal(403, ((ObjectResult)result.Result!).StatusCode);
    }

    [Fact]
    public async Task WordFilter_CaseSensitive_OnlyMatchesExactCase()
    {
        await SeedAsync();
        _db.Db.WordFilters.Add(new WordFilter { ServerId = _server.Id, Pattern = "Bad", CaseSensitive = true, Severity = WordFilterSeverity.Delete });
        await _db.Db.SaveChangesAsync();
        TestHelpers.SetUser(_msgCtrl, _member.Id, _member.Username);

        // lowercase "bad" should NOT match case-sensitive "Bad"
        var result = await _msgCtrl.SendMessage(ChannelId, new SendMessageRequest("this is bad"));
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<MessageDto>(ok.Value);
    }
}

public class EveryoneMentionGatingTests : IDisposable
{
    private readonly TestDb _db;
    private readonly MessagesController _msgCtrl;
    private readonly ServersController _serverCtrl;
    private GuildServer _server = null!;
    private User _admin = null!;
    private User _member = null!;

    public EveryoneMentionGatingTests()
    {
        _db = new TestDb();
        _msgCtrl = new MessagesController(_db.Db, TestHelpers.MockHub(),
            new ServerMetricsService(), TestHelpers.NullBot(), TestHelpers.NullAutomod(), TestHelpers.NullWebhooks());
        _serverCtrl = new ServersController(_db.Db, TestHelpers.MockHub(), TestHelpers.NullWebhooks());
    }

    public void Dispose() => _db.Dispose();

    private async Task SeedAsync()
    {
        (_server, _admin) = await TestHelpers.SeedServerAsync(_db.Db);
        _member = new User { Username = "member", Email = "m@test.com", PasswordHash = "*" };
        _db.Db.Users.Add(_member);
        await _db.Db.SaveChangesAsync();
        _db.Db.ServerMembers.Add(new ServerMember { ServerId = _server.Id, UserId = _member.Id, Role = ServerRole.Member });
        await _db.Db.SaveChangesAsync();
    }

    private int ChannelId => _db.Db.Channels.First(c => c.ServerId == _server.Id && c.Type == ChannelType.Text).Id;

    [Fact]
    public async Task Member_CannotMentionEveryone_WhenGated()
    {
        await SeedAsync();
        TestHelpers.SetUser(_msgCtrl, _member.Id, _member.Username);

        var result = await _msgCtrl.SendMessage(ChannelId, new SendMessageRequest("@everyone hello"));

        Assert.Equal(403, ((ObjectResult)result.Result!).StatusCode);
    }

    [Fact]
    public async Task Admin_CanMentionEveryone()
    {
        await SeedAsync();
        TestHelpers.SetUser(_msgCtrl, _admin.Id, _admin.Username);

        var result = await _msgCtrl.SendMessage(ChannelId, new SendMessageRequest("@everyone hello"));

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task Member_CanMentionEveryone_WhenRoleSetToMember()
    {
        await SeedAsync();
        _server.MinRoleToMentionEveryone = ServerRole.Member;
        await _db.Db.SaveChangesAsync();
        TestHelpers.SetUser(_msgCtrl, _member.Id, _member.Username);

        var result = await _msgCtrl.SendMessage(ChannelId, new SendMessageRequest("@everyone hello"));

        Assert.IsType<OkObjectResult>(result.Result);
    }
}

public class GroupDmsTests : IDisposable
{
    private readonly TestDb _db;
    private readonly GroupDmsController _ctrl;
    private User _alice = null!;
    private User _bob = null!;

    public GroupDmsTests()
    {
        _db = new TestDb();
        _ctrl = new GroupDmsController(_db.Db, TestHelpers.MockHub());
    }

    public void Dispose() => _db.Dispose();

    private async Task SeedAsync()
    {
        _alice = new User { Username = "alice", Email = "alice@test.com", PasswordHash = "*" };
        _bob = new User { Username = "bob", Email = "bob@test.com", PasswordHash = "*" };
        _db.Db.Users.AddRange(_alice, _bob);
        await _db.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task Create_WithValidMembers_ReturnsDto()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _alice.Id, _alice.Username);

        var result = await _ctrl.Create(new CreateGroupConversationRequest("Test Group", [_bob.Id]));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<GroupConversationDto>(ok.Value);
        Assert.Equal("Test Group", dto.Name);
        Assert.Equal(2, dto.Members.Count);
    }

    [Fact]
    public async Task Create_WithNoOtherMembers_ReturnsBadRequest()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _alice.Id, _alice.Username);

        var result = await _ctrl.Create(new CreateGroupConversationRequest("Test Group", []));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Create_WithBlockedMember_ReturnsBadRequest()
    {
        await SeedAsync();
        // bob blocked alice — alice must not be able to pull bob into a group.
        _db.Db.UserBlocks.Add(new UserBlock { BlockerId = _bob.Id, BlockedId = _alice.Id });
        await _db.Db.SaveChangesAsync();
        TestHelpers.SetUser(_ctrl, _alice.Id, _alice.Username);

        var result = await _ctrl.Create(new CreateGroupConversationRequest("Test Group", [_bob.Id]));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(_db.Db.GroupConversations);
    }

    [Fact]
    public async Task SendMessage_AsMember_Succeeds()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _alice.Id, _alice.Username);
        var created = ((OkObjectResult)(await _ctrl.Create(new CreateGroupConversationRequest("Test", [_bob.Id]))).Result!).Value as GroupConversationDto;

        var result = await _ctrl.SendMessage(created!.Id, new SendGroupMessageRequest("Hello group!"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<GroupMessageDto>(ok.Value);
        Assert.Equal("Hello group!", dto.Content);
    }

    [Fact]
    public async Task Leave_AsMember_Succeeds()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _alice.Id, _alice.Username);
        var created = ((OkObjectResult)(await _ctrl.Create(new CreateGroupConversationRequest("Test", [_bob.Id]))).Result!).Value as GroupConversationDto;

        TestHelpers.SetUser(_ctrl, _bob.Id, _bob.Username);
        var result = await _ctrl.Leave(created!.Id);

        Assert.IsType<NoContentResult>(result);
    }
}

public class SessionBlacklistTests
{
    [Fact]
    public void Revoke_ThenIsRevoked_ReturnsTrue()
    {
        var svc = new SessionBlacklistService();
        var hash = SessionBlacklistService.HashToken("some-jwt-token");
        svc.Revoke(hash);
        Assert.True(svc.IsRevoked(hash));
    }

    [Fact]
    public void IsRevoked_UnknownToken_ReturnsFalse()
    {
        var svc = new SessionBlacklistService();
        Assert.False(svc.IsRevoked("nonexistent-hash"));
    }

    [Fact]
    public void HashToken_SameInput_SameOutput()
    {
        var h1 = SessionBlacklistService.HashToken("token-abc");
        var h2 = SessionBlacklistService.HashToken("token-abc");
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void HashToken_DifferentInputs_DifferentOutputs()
    {
        var h1 = SessionBlacklistService.HashToken("token-a");
        var h2 = SessionBlacklistService.HashToken("token-b");
        Assert.NotEqual(h1, h2);
    }
}

public class LeetSpeakNormalizerTests
{
    [Theory]
    [InlineData("h3ll0", "hello")]
    [InlineData("b4d", "bad")]
    [InlineData("$p4m", "spam")]
    [InlineData("fr33", "free")]
    [InlineData("normal", "normal")]
    public void Normalize_ReturnsExpected(string input, string expected)
    {
        Assert.Equal(expected, LeetSpeakNormalizer.Normalize(input));
    }
}

public class WarningsControllerTests : IDisposable
{
    private readonly TestDb _db;
    private readonly WarningsController _ctrl;
    private GuildServer _server = null!;
    private User _admin = null!;
    private User _member = null!;

    public WarningsControllerTests()
    {
        _db = new TestDb();
        _ctrl = new WarningsController(_db.Db);
    }

    public void Dispose() => _db.Dispose();

    private async Task SeedAsync()
    {
        (_server, _admin) = await TestHelpers.SeedServerAsync(_db.Db);
        _member = new User { Username = "member", Email = "m@test.com", PasswordHash = "*" };
        _db.Db.Users.Add(_member);
        await _db.Db.SaveChangesAsync();
        _db.Db.ServerMembers.Add(new ServerMember { ServerId = _server.Id, UserId = _member.Id, Role = ServerRole.Member });
        await _db.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task AddWarning_AsAdmin_Succeeds()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _admin.Id, _admin.Username);

        var result = await _ctrl.AddWarning(_server.Id, _member.Id, new AddWarningRequest("Rule violation"));

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(1, await _db.Db.UserWarnings.CountAsync(w => w.UserId == _member.Id));
    }

    [Fact]
    public async Task AddWarning_AsMember_ReturnsForbid()
    {
        await SeedAsync();
        TestHelpers.SetUser(_ctrl, _member.Id, _member.Username);

        var result = await _ctrl.AddWarning(_server.Id, _admin.Id, new AddWarningRequest("Test"));

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetWarnings_AsAdmin_ReturnsList()
    {
        await SeedAsync();
        _db.Db.UserWarnings.Add(new UserWarning { ServerId = _server.Id, UserId = _member.Id, ActorId = _admin.Id, ActorUsername = _admin.Username, Reason = "Test" });
        await _db.Db.SaveChangesAsync();
        TestHelpers.SetUser(_ctrl, _admin.Id, _admin.Username);

        var result = await _ctrl.GetWarnings(_server.Id, _member.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<UserWarningDto>>(ok.Value);
        Assert.Single(list);
    }
}
