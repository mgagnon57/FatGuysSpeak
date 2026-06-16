namespace FatGuysSpeak.Shared;

public static class NotificationRules
{
    public static bool ShouldToastDm(int authorId, int currentUserId, bool isActiveConvo)
        => authorId != currentUserId && !isActiveConvo;

    public static bool IsMentionOf(string? content, string? username)
        => !string.IsNullOrEmpty(content)
        && !string.IsNullOrEmpty(username)
        && content.Contains($"@{username}", StringComparison.OrdinalIgnoreCase);
}
