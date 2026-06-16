using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Tests.Server;

public class WordFilterTests : IDisposable
{
    private readonly TestDb _testDb;
    private readonly WordFiltersController _filterCtrl;
    private readonly MessagesController _msgCtrl;
    private GuildServer _server = null!;
    private User _admin = null!;
    private User _member = null!;

    public WordFilterTests()
    {
        _testDb = new TestDb();
        _filterCtrl = new WordFiltersController(_testDb.Db);
        _msgCtrl = new MessagesController(_testDb.Db, TestHelpers.MockHub(),
            new FatGuysSpeak.Server.Services.ServerMetricsService(), TestHelpers.NullBot(), TestHelpers.NullAutomod(), TestHelpers.NullWebhooks());
    }

    public void Dispose() => _testDb.Dispose();

    private async Task SeedAsync()
    {
        (_server, _admin) = await TestHelpers.SeedServerAsync(_testDb.Db);
        _member = new User { Username = "member", Email = "m@test.com", PasswordHash = "*" };
        _testDb.Db.Users.Add(_member);
        await _testDb.Db.SaveChangesAsync();
        _testDb.Db.ServerMembers.Add(new ServerMember
            { ServerId = _server.Id, UserId = _member.Id, Role = ServerRole.Member });
        await _testDb.Db.SaveChangesAsync();
    }

    private int ChannelId => _testDb.Db.Channels
        .First(c => c.ServerId == _server.Id && c.Type == ChannelType.Text).Id;

    [Fact]
    public async Task AddFilter_AsAdmin_Succeeds()
    {
        await SeedAsync();
        TestHelpers.SetUser(_filterCtrl, _admin.Id, _admin.Username);

        var result = await _filterCtrl.AddFilter(_server.Id, new AddWordFilterRequest("badword"));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<WordFilterDto>(ok.Value);
        Assert.Equal("badword", dto.Pattern);
    }

    [Fact]
    public async Task AddFilter_AsMember_ReturnsForbid()
    {
        await SeedAsync();
        TestHelpers.SetUser(_filterCtrl, _member.Id, _member.Username);

        var result = await _filterCtrl.AddFilter(_server.Id, new AddWordFilterRequest("badword"));

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task AddFilter_Duplicate_ReturnsConflict()
    {
        await SeedAsync();
        TestHelpers.SetUser(_filterCtrl, _admin.Id, _admin.Username);
        await _filterCtrl.AddFilter(_server.Id, new AddWordFilterRequest("badword"));

        var result = await _filterCtrl.AddFilter(_server.Id, new AddWordFilterRequest("badword"));

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task AddFilter_CaseInsensitiveDuplicate_ReturnsConflict()
    {
        await SeedAsync();
        TestHelpers.SetUser(_filterCtrl, _admin.Id, _admin.Username);
        await _filterCtrl.AddFilter(_server.Id, new AddWordFilterRequest("BadWord"));

        var result = await _filterCtrl.AddFilter(_server.Id, new AddWordFilterRequest("badword"));

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task RemoveFilter_AsAdmin_Succeeds()
    {
        await SeedAsync();
        TestHelpers.SetUser(_filterCtrl, _admin.Id, _admin.Username);
        var added = ((OkObjectResult)(await _filterCtrl.AddFilter(_server.Id, new AddWordFilterRequest("bad"))).Result!).Value as WordFilterDto;

        var result = await _filterCtrl.RemoveFilter(_server.Id, added!.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.False(await _testDb.Db.WordFilters.AnyAsync(f => f.Id == added.Id));
    }

    [Fact]
    public async Task SendMessage_WithFilteredWord_ReplacesWithAsterisks()
    {
        await SeedAsync();
        _testDb.Db.WordFilters.Add(new WordFilter { ServerId = _server.Id, Pattern = "badword", Severity = WordFilterSeverity.Log });
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_msgCtrl, _member.Id, _member.Username);

        var result = await _msgCtrl.SendMessage(ChannelId, new SendMessageRequest("Hello badword world"));

        var dto = (MessageDto)((OkObjectResult)result.Result!).Value!;
        Assert.Equal("Hello ******* world", dto.Content);
    }

    [Fact]
    public async Task SendMessage_FilterIsCaseInsensitive()
    {
        await SeedAsync();
        _testDb.Db.WordFilters.Add(new WordFilter { ServerId = _server.Id, Pattern = "badword", Severity = WordFilterSeverity.Log });
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_msgCtrl, _member.Id, _member.Username);

        var result = await _msgCtrl.SendMessage(ChannelId, new SendMessageRequest("Hello BADWORD world"));

        var dto = (MessageDto)((OkObjectResult)result.Result!).Value!;
        Assert.Equal("Hello ******* world", dto.Content);
    }

    [Fact]
    public async Task SendMessage_PhraseFilter_ReplacesPhrase()
    {
        await SeedAsync();
        _testDb.Db.WordFilters.Add(new WordFilter { ServerId = _server.Id, Pattern = "bad phrase", Severity = WordFilterSeverity.Log });
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_msgCtrl, _member.Id, _member.Username);

        var result = await _msgCtrl.SendMessage(ChannelId, new SendMessageRequest("This is a bad phrase here"));

        var dto = (MessageDto)((OkObjectResult)result.Result!).Value!;
        Assert.Equal("This is a ********** here", dto.Content);
    }

    [Fact]
    public async Task SendMessage_AdminBypasses_Filter()
    {
        await SeedAsync();
        _testDb.Db.WordFilters.Add(new WordFilter { ServerId = _server.Id, Pattern = "badword" });
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_msgCtrl, _admin.Id, _admin.Username);

        var result = await _msgCtrl.SendMessage(ChannelId, new SendMessageRequest("Hello badword world"));

        var dto = (MessageDto)((OkObjectResult)result.Result!).Value!;
        Assert.Equal("Hello badword world", dto.Content);
    }

    [Fact]
    public async Task Apply_WordBoundary_DoesNotMatchSubstring()
    {
        var filters = new List<WordFilter> { new WordFilter { Pattern = "ass" } };
        var result = WordFiltersController.Apply("assassination", filters);
        Assert.Equal("assassination", result.FilteredContent);
    }

    [Fact]
    public async Task GetFilters_ReturnsList()
    {
        await SeedAsync();
        _testDb.Db.WordFilters.Add(new WordFilter { ServerId = _server.Id, Pattern = "word1" });
        _testDb.Db.WordFilters.Add(new WordFilter { ServerId = _server.Id, Pattern = "word2" });
        await _testDb.Db.SaveChangesAsync();
        TestHelpers.SetUser(_filterCtrl, _admin.Id, _admin.Username);

        var result = await _filterCtrl.GetFilters(_server.Id);

        Assert.Equal(2, result.Value!.Count);
    }
}
