using CommunityToolkit.Mvvm.ComponentModel;

namespace FatGuysSpeak.Client.ViewModels;

public partial class VideoTileViewModel : ObservableObject
{
    public int    UserId   { get; }
    public string Username { get; }
    public bool   IsLocal  { get; }

    [ObservableProperty] private ImageSource? _frame;

    public VideoTileViewModel(int userId, string username, bool isLocal = false)
    {
        UserId   = userId;
        Username = isLocal ? $"{username} (You)" : username;
        IsLocal  = isLocal;
    }

    public void UpdateFrame(byte[] jpeg) =>
        MainThread.BeginInvokeOnMainThread(() =>
            Frame = ImageSource.FromStream(() => new MemoryStream(jpeg)));
}
