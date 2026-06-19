using CommunityToolkit.Mvvm.ComponentModel;
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Client.ViewModels;

// A user shown in a channel's occupant list. Wraps the immutable UserDto so each row can carry a
// transient IsSpeaking flag (lit by the same UserSpeaking signal the voice panel uses).
public partial class OccupantViewModel(UserDto user) : ObservableObject
{
    public int     Id        => user.Id;
    public string  Username  => user.Username;
    public string? AvatarUrl => user.AvatarUrl;

    [ObservableProperty] private bool _isSpeaking;

    private CancellationTokenSource? _silenceCts;

    // Light up on a voice packet, then auto-clear shortly after the talking stops.
    public void SetSpeaking()
    {
        _silenceCts?.Cancel();
        IsSpeaking = true;
        _silenceCts = new CancellationTokenSource();
        var token = _silenceCts.Token;
        Task.Delay(600, token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
                MainThread.BeginInvokeOnMainThread(() => IsSpeaking = false);
        }, TaskScheduler.Default);
    }
}
