using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Client.ViewModels;

// Interactive state for one poll card in the message stream.
public partial class PollViewModel : ObservableObject
{
    public int PollId { get; }
    public string Question { get; }
    public ObservableCollection<PollOptionViewModel> Options { get; } = [];

    [ObservableProperty] private int _totalVotes;
    [ObservableProperty] private int? _myVoteOptionId;

    public PollViewModel(PollDto dto)
    {
        PollId = dto.Id;
        Question = dto.Question;
        foreach (var o in dto.Options)
            Options.Add(new PollOptionViewModel(PollId, o.Id, o.Text));
        Apply(dto, applyMyVote: true);
    }

    public string TotalVotesLabel => TotalVotes == 1 ? "1 vote" : $"{TotalVotes} votes";

    // Update tallies from a shared broadcast (no per-user vote). Keeps this client's own highlight.
    public void ApplyTallies(PollDto dto) => Apply(dto, applyMyVote: false);

    // Update tallies AND this client's own vote (from the voter's own response).
    public void ApplyMyVote(PollDto dto) => Apply(dto, applyMyVote: true);

    private void Apply(PollDto dto, bool applyMyVote)
    {
        TotalVotes = dto.TotalVotes;
        if (applyMyVote) MyVoteOptionId = dto.MyVoteOptionId;
        foreach (var od in dto.Options)
        {
            var vm = Options.FirstOrDefault(o => o.Id == od.Id);
            if (vm is null) continue;
            vm.Votes = od.Votes;
            vm.Fraction = dto.TotalVotes > 0 ? (double)od.Votes / dto.TotalVotes : 0;
            vm.IsMyVote = MyVoteOptionId == od.Id;
        }
        OnPropertyChanged(nameof(TotalVotesLabel));
    }
}

public partial class PollOptionViewModel(int pollId, int id, string text) : ObservableObject
{
    public int PollId { get; } = pollId;
    public int Id { get; } = id;
    public string Text { get; } = text;

    [ObservableProperty] private int _votes;
    [ObservableProperty] private double _fraction;   // 0..1 share of total, for the result bar
    [ObservableProperty] private bool _isMyVote;

    public string VotesLabel => Fraction > 0 ? $"{Votes} · {Fraction:P0}" : $"{Votes}";
    partial void OnVotesChanged(int value) => OnPropertyChanged(nameof(VotesLabel));
    partial void OnFractionChanged(double value) => OnPropertyChanged(nameof(VotesLabel));
}
