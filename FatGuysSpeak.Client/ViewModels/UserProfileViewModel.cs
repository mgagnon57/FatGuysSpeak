using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FatGuysSpeak.Client.Services;
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Client.ViewModels;

public partial class UserProfileViewModel(ApiService api, int serverId) : ObservableObject
{
    [ObservableProperty] private UserProfileDto? _profile;
    [ObservableProperty] private bool _isLoading = true;

    public string AvatarText => Profile is null ? "?"
        : Profile.Username.Length >= 2
            ? $"{Profile.Username[0]}{Profile.Username[1]}".ToUpper()
            : Profile.Username[0].ToString().ToUpper();

    public string StatusText => Profile?.Status switch
    {
        UserStatus.Online => "Online",
        UserStatus.Away => "Away",
        UserStatus.DoNotDisturb => "Do Not Disturb",
        _ => "Offline"
    };

    public Color StatusColor => Profile?.Status switch
    {
        UserStatus.Online => Color.FromArgb("#23a55a"),
        UserStatus.Away => Color.FromArgb("#f0a030"),
        UserStatus.DoNotDisturb => Color.FromArgb("#ed4245"),
        _ => Color.FromArgb("#555555")
    };

    public string RoleText => Profile?.Role switch
    {
        ServerRole.Admin => "👑 Admin",
        ServerRole.Moderator => "🛡 Moderator",
        ServerRole.Member => "👤 Member",
        _ => ""
    };

    public bool HasRole => Profile?.Role.HasValue ?? false;

    public string JoinedText => Profile?.JoinedAt is DateTime d
        ? $"Member since {d:MMMM yyyy}"
        : Profile?.CreatedAt is DateTime c
            ? $"Account created {c:MMMM yyyy}"
            : "";

    public bool IsOwnProfile => Profile?.IsCurrentUser ?? false;

    public async Task LoadAsync(int userId)
    {
        IsLoading = true;
        Profile = await api.GetUserProfileAsync(userId, serverId);
        IsLoading = false;
        OnPropertyChanged(nameof(AvatarText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(RoleText));
        OnPropertyChanged(nameof(HasRole));
        OnPropertyChanged(nameof(JoinedText));
        OnPropertyChanged(nameof(IsOwnProfile));
    }

    [RelayCommand]
    public async Task SetStatus(string statusStr)
    {
        if (!Enum.TryParse<UserStatus>(statusStr, out var status)) return;
        await api.UpdateStatusAsync(status);
        if (Profile is not null)
        {
            Profile = Profile with { Status = status };
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusColor));
        }
    }

    partial void OnProfileChanged(UserProfileDto? value)
    {
        OnPropertyChanged(nameof(AvatarText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(RoleText));
        OnPropertyChanged(nameof(HasRole));
        OnPropertyChanged(nameof(JoinedText));
        OnPropertyChanged(nameof(IsOwnProfile));
    }
}
