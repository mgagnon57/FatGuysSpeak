using CommunityToolkit.Mvvm.ComponentModel;
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Client.ViewModels;

public partial class MemberViewItem : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoleIcon), nameof(HasRoleIcon))]
    private ServerRole _role;

    [ObservableProperty] private UserStatus _status;

    public int Id { get; }
    public string Username { get; }
    public string? AvatarUrl { get; }

    public string RoleIcon => Role switch
    {
        ServerRole.Admin => "👑",
        ServerRole.Moderator => "🛡",
        _ => ""
    };

    public bool HasRoleIcon => Role >= ServerRole.Moderator;

    public MemberViewItem(UserDto user, ServerRole role = ServerRole.Member)
    {
        Id = user.Id;
        Username = user.Username;
        AvatarUrl = user.AvatarUrl;
        _role = role;
        _status = user.Status;
    }
}
