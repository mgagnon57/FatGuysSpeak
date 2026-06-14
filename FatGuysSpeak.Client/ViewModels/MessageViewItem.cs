using CommunityToolkit.Mvvm.ComponentModel;
using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Client.ViewModels;

public partial class MessageViewItem : ObservableObject
{
    private static int _systemIdCounter = -1;

    // Message is observable so MAUI re-evaluates nested bindings (Content, IsDeleted, etc.)
    // when ApplyEdit replaces the backing record.
    [ObservableProperty] private MessageDto _message;
    [ObservableProperty] private bool _isEdited;
    [ObservableProperty] private LinkPreviewDto? _preview;

    public bool IsSystemMessage { get; private init; }
    public bool IsMention { get; private init; }
    public bool IsOwnMessage { get; private init; }

    public bool HasAttachment => Message.AttachmentUrl is not null;

    public MessageViewItem(MessageDto message, string currentUsername = "", int currentUserId = 0)
    {
        _message = message;
        _isEdited = message.EditedAt.HasValue;
        IsSystemMessage = false;
        IsOwnMessage = message.AuthorId != 0 && message.AuthorId == currentUserId;
        IsMention = !string.IsNullOrEmpty(currentUsername)
            && message.AuthorId != 0
            && message.AuthorUsername != currentUsername
            && message.Content.Contains($"@{currentUsername}", StringComparison.OrdinalIgnoreCase);
    }

    public void ApplyEdit(MessageDto updated)
    {
        Message = updated;
        IsEdited = updated.EditedAt.HasValue;
    }

    public void ApplyDelete()
    {
        Message = Message with { IsDeleted = true };
    }

    partial void OnMessageChanged(MessageDto value) =>
        OnPropertyChanged(nameof(HasAttachment));

    public static MessageViewItem CreateSystem(string text, int channelId, MessageSource source = MessageSource.Text)
    {
        var id = Interlocked.Decrement(ref _systemIdCounter);
        var dto = new MessageDto(id, text, string.Empty, 0, DateTime.UtcNow, channelId, source);
        return new MessageViewItem(dto) { IsSystemMessage = true };
    }
}
