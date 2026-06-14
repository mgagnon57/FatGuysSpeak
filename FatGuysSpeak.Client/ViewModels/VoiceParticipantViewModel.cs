using CommunityToolkit.Mvvm.ComponentModel;
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Client.ViewModels;

public partial class VoiceParticipantViewModel(VoiceStateDto state) : ObservableObject
{
    public VoiceStateDto State { get; } = state;

    public int    UserId   => State.UserId;
    public string Username => State.Username;
    public bool   Muted    => State.Muted;
    public bool   Deafened => State.Deafened;

    [ObservableProperty] private bool _isSpeaking;

    public Color         DotColor       => IsSpeaking ? Color.FromArgb("#44ee44") : Color.FromArgb("#2a3a2a");
    public Color         NameColor      => IsSpeaking ? Color.FromArgb("#ffffff") : Color.FromArgb("#8ecf8e");
    public FontAttributes NameFont      => IsSpeaking ? FontAttributes.Bold : FontAttributes.None;

    private CancellationTokenSource? _silenceCts;

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

    partial void OnIsSpeakingChanged(bool value)
    {
        OnPropertyChanged(nameof(DotColor));
        OnPropertyChanged(nameof(NameColor));
        OnPropertyChanged(nameof(NameFont));
    }
}
