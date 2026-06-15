using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FatGuysSpeak.Client.Services;
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Client.ViewModels;

public partial class UserProfileViewModel : ObservableObject
{
    private readonly ApiService _api;
    private readonly int _serverId;

    public UserProfileViewModel(ApiService api, int serverId, bool isBlocked = false)
    {
        _api = api;
        _serverId = serverId;
        _isBlocked = isBlocked;
    }

    [ObservableProperty] private UserProfileDto? _profile;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isEditingBio;
    [ObservableProperty] private string _bioInput = "";
    [ObservableProperty] private string _bioError = "";
    [ObservableProperty] private bool _isBlocked;

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
    public bool HasBio => !string.IsNullOrWhiteSpace(Profile?.Bio);
    public string BlockButtonText => IsBlocked ? "🔓 Unblock User" : "🚫 Block User";

    public async Task LoadAsync(int userId)
    {
        IsLoading = true;
        Profile = await _api.GetUserProfileAsync(userId, _serverId);
        BioInput = Profile?.Bio ?? "";
        IsLoading = false;
        NotifyAll();
    }

    [RelayCommand]
    public async Task SetStatus(string statusStr)
    {
        if (!Enum.TryParse<UserStatus>(statusStr, out var status)) return;
        await _api.UpdateStatusAsync(status);
        if (Profile is not null)
        {
            Profile = Profile with { Status = status };
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusColor));
        }
    }

    [RelayCommand]
    public void StartEditBio()
    {
        BioInput = Profile?.Bio ?? "";
        BioError = "";
        IsEditingBio = true;
    }

    [RelayCommand]
    public void CancelEditBio()
    {
        IsEditingBio = false;
        BioError = "";
    }

    [RelayCommand]
    public async Task SaveBioAsync()
    {
        if (BioInput.Length > 300)
        {
            BioError = "Bio must be 300 characters or fewer.";
            return;
        }
        var ok = await _api.UpdateBioAsync(BioInput);
        if (!ok)
        {
            BioError = "Failed to save bio.";
            return;
        }
        Profile = Profile! with { Bio = string.IsNullOrWhiteSpace(BioInput) ? null : BioInput.Trim() };
        IsEditingBio = false;
        BioError = "";
        OnPropertyChanged(nameof(HasBio));
    }

    [RelayCommand]
    public async Task ToggleBlockAsync()
    {
        if (Profile is null) return;
        if (IsBlocked)
        {
            var ok = await _api.UnblockUserAsync(Profile.Id);
            if (ok)
            {
                IsBlocked = false;
                OnPropertyChanged(nameof(BlockButtonText));
            }
        }
        else
        {
            var ok = await _api.BlockUserAsync(Profile.Id);
            if (ok)
            {
                IsBlocked = true;
                OnPropertyChanged(nameof(BlockButtonText));
            }
        }
    }

    partial void OnProfileChanged(UserProfileDto? value) => NotifyAll();

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(AvatarText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(RoleText));
        OnPropertyChanged(nameof(HasRole));
        OnPropertyChanged(nameof(JoinedText));
        OnPropertyChanged(nameof(IsOwnProfile));
        OnPropertyChanged(nameof(HasBio));
        OnPropertyChanged(nameof(BlockButtonText));
    }
}
