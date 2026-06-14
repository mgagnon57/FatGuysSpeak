using CommunityToolkit.Mvvm.ComponentModel;
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Client.ViewModels;

public partial class MessageViewItem : ObservableObject
{
    private static int _systemIdCounter = -1;

    public MessageViewItem(MessageDto message) { Message = message; }

    public MessageDto Message { get; }
    public bool IsSystemMessage { get; private init; }

    [ObservableProperty] private LinkPreviewDto? _preview;

    public static MessageViewItem CreateSystem(string text, int channelId, MessageSource source = MessageSource.Text)
    {
        var id = Interlocked.Decrement(ref _systemIdCounter);
        var dto = new MessageDto(id, text, string.Empty, 0, DateTime.UtcNow, channelId, source);
        return new MessageViewItem(dto) { IsSystemMessage = true };
    }
}
