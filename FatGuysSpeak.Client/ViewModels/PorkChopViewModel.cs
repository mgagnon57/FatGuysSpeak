using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FatGuysSpeak.Client.Services;

namespace FatGuysSpeak.Client.ViewModels;

/// <summary>One line in the PorkChop tab — either the user's question or PorkChop's answer.</summary>
public partial class PorkChopTurnViewItem : ObservableObject
{
    public PorkChopTurnViewItem(bool isUser, string text)
    {
        IsUser = isUser;
        Sender = isUser ? "You" : "🐷 PorkChop";
        _text = text;
    }

    public bool IsUser { get; }
    public string Sender { get; }
    public string Time { get; } = DateTime.Now.ToString("h:mm tt");
    [ObservableProperty] private string _text;

    // Only PorkChop's answers can be shared to the channel — never the user's own questions.
    public bool CanShare => !IsUser;

    // Flips to true once shared, so the button becomes a one-shot confirmation.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShareButtonText))]
    private bool _isShared;

    public string ShareButtonText => IsShared ? "Shared to channel ✓" : "Share with channel";
}

/// <summary>
/// The ephemeral @PorkChop tab. The whole conversation lives only in this in-memory collection and
/// is never persisted — registered as a singleton so it survives navigating in and out of the tab,
/// but it clears the moment the app closes. Answers come from POST /api/porkchop/ask, which also
/// stores nothing server-side.
/// </summary>
public partial class PorkChopViewModel(ApiService api) : ObservableObject
{
    public ObservableCollection<PorkChopTurnViewItem> Turns { get; } = [];

    [ObservableProperty] private string _draft = "";
    [ObservableProperty] private bool _isBusy;

    public bool HasNoTurns => Turns.Count == 0;

    [RelayCommand]
    private async Task SendAsync()
    {
        var question = Draft?.Trim();
        if (string.IsNullOrEmpty(question) || IsBusy) return;

        Draft = "";
        Turns.Add(new PorkChopTurnViewItem(isUser: true, question));
        OnPropertyChanged(nameof(HasNoTurns));
        var answerItem = new PorkChopTurnViewItem(isUser: false, "…");
        Turns.Add(answerItem);

        IsBusy = true;
        try
        {
            var answer = await api.AskPorkChopAsync(question);
            answerItem.Text = answer ?? "PorkChop isn't around right now — try again in a bit.";
        }
        finally { IsBusy = false; }
    }

    // Opt-in: a PorkChop answer is private to this tab unless the user chooses to post it into the
    // channel they're currently viewing. Posts as a normal message from the user.
    [RelayCommand]
    private async Task ShareWithChannelAsync(PorkChopTurnViewItem? turn)
    {
        if (turn is null || turn.IsUser || turn.IsShared) return;
        if (string.IsNullOrWhiteSpace(turn.Text) || turn.Text == "…") return;

        var channelId = api.CurrentChannelId;
        if (channelId is null)
        {
            await Shell.Current.DisplayAlert(
                "Share with channel", "Open a channel first, then share this answer.", "OK");
            return;
        }

        var content = $"🐷 PorkChop (shared): {turn.Text}";
        var (dto, error) = await api.SendMessageAsync(channelId.Value, content);
        if (dto is not null)
            turn.IsShared = true;
        else
            await Shell.Current.DisplayAlert(
                "Share with channel", error ?? "Couldn't share that answer — try again.", "OK");
    }
}
