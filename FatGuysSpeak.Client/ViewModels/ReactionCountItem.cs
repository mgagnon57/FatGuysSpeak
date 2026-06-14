using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FatGuysSpeak.Client.ViewModels;

public partial class ReactionCountItem : ObservableObject
{
    public string Emoji { get; }
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isOwn;

    public string Display => $"{Emoji} {Count}";
    public IRelayCommand ToggleCommand { get; }

    public ReactionCountItem(string emoji, int count, bool isOwn, Action<string> onToggle)
    {
        Emoji = emoji;
        _count = count;
        _isOwn = isOwn;
        ToggleCommand = new RelayCommand(() => onToggle(emoji));
    }

    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(Display));
}
