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
}
