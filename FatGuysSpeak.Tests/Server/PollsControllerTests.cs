using FatGuysSpeak.Server.Controllers;
using FatGuysSpeak.Server.Models;
using FatGuysSpeak.Shared;
using FatGuysSpeak.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Tests.Server;

public class PollsControllerTests : IDisposable
{
    private readonly TestDb _db;
    private readonly PollsController _controller;
    private GuildServer _server = null!;
    private Channel _channel = null!;
    private User _user = null!;

    public PollsControllerTests()
    {
        _db = new TestDb();
        _controller = new PollsController(_db.Db, TestHelpers.MockHub());
    }

    public void Dispose() => _db.Dispose();

    private async Task SeedAsync()
    {
        (_server, _user) = await TestHelpers.SeedServerAsync(_db.Db);
        _channel = _db.Db.Channels.First(c => c.ServerId == _server.Id && c.Type == ChannelType.Text);
        TestHelpers.SetUser(_controller, _user.Id, _user.Username);
    }

    private static MessageDto Created(ActionResult<MessageDto> r) =>
        (MessageDto)((OkObjectResult)r.Result!).Value!;

    private static PollDto Voted(ActionResult<PollDto> r) =>
        (PollDto)((OkObjectResult)r.Result!).Value!;

    [Fact]
    public async Task Create_PostsPollMessageWithOptions()
    {
        await SeedAsync();

        var result = await _controller.Create(_channel.Id, new CreatePollRequest("Game tonight?", ["Apex", "Valorant", "CS2"]));

        var dto = Created(result);
        Assert.NotNull(dto.Poll);
        Assert.Equal("Game tonight?", dto.Poll!.Question);
        Assert.Equal(3, dto.Poll.Options.Count);
        Assert.Equal(0, dto.Poll.TotalVotes);
        Assert.True(_db.Db.Messages.Any(m => m.PollId == dto.Poll.Id));   // posted as a message card
    }

    [Fact]
    public async Task Create_TooFewOptions_ReturnsBadRequest()
    {
        await SeedAsync();
        var result = await _controller.Create(_channel.Id, new CreatePollRequest("One choice?", ["only one"]));
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Vote_RecordsTallyAndMyVote()
    {
        await SeedAsync();
        var poll = Created(await _controller.Create(_channel.Id, new CreatePollRequest("Q?", ["A", "B"]))).Poll!;
        var optionA = poll.Options[0].Id;

        var result = Voted(await _controller.Vote(poll.Id, new PollVoteRequest(optionA)));

        Assert.Equal(1, result.TotalVotes);
        Assert.Equal(optionA, result.MyVoteOptionId);
        Assert.Equal(1, result.Options.First(o => o.Id == optionA).Votes);
    }

    [Fact]
    public async Task Vote_ChangingChoice_KeepsSingleVote()
    {
        await SeedAsync();
        var poll = Created(await _controller.Create(_channel.Id, new CreatePollRequest("Q?", ["A", "B"]))).Poll!;
        var (a, b) = (poll.Options[0].Id, poll.Options[1].Id);

        await _controller.Vote(poll.Id, new PollVoteRequest(a));
        var result = Voted(await _controller.Vote(poll.Id, new PollVoteRequest(b)));

        Assert.Equal(1, result.TotalVotes);
        Assert.Equal(b, result.MyVoteOptionId);
        Assert.Equal(0, result.Options.First(o => o.Id == a).Votes);
    }

    [Fact]
    public async Task Vote_TappingSameChoice_Retracts()
    {
        await SeedAsync();
        var poll = Created(await _controller.Create(_channel.Id, new CreatePollRequest("Q?", ["A", "B"]))).Poll!;
        var a = poll.Options[0].Id;

        await _controller.Vote(poll.Id, new PollVoteRequest(a));
        var result = Voted(await _controller.Vote(poll.Id, new PollVoteRequest(a)));

        Assert.Equal(0, result.TotalVotes);
        Assert.Null(result.MyVoteOptionId);
    }

    [Fact]
    public async Task Vote_OptionFromAnotherPoll_ReturnsBadRequest()
    {
        await SeedAsync();
        var poll1 = Created(await _controller.Create(_channel.Id, new CreatePollRequest("Q1", ["A", "B"]))).Poll!;
        var poll2 = Created(await _controller.Create(_channel.Id, new CreatePollRequest("Q2", ["C", "D"]))).Poll!;

        var result = await _controller.Vote(poll1.Id, new PollVoteRequest(poll2.Options[0].Id));
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }
}
