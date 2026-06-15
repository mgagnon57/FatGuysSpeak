using CommunityToolkit.Mvvm.ComponentModel;
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Client.ViewModels;

public partial class ServerViewItem : ObservableObject
{
    public ServerDto Server { get; init; } = null!;
    [ObservableProperty] private bool _isSelected;

    public string Initials => Server.Name.Length > 0
        ? Server.Name[..1].ToUpperInvariant()
        : "?";

    public static ServerViewItem FromDto(ServerDto dto) => new() { Server = dto };
}
