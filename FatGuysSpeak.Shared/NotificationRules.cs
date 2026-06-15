namespace FatGuysSpeak.Shared;

public static class NotificationRules
{
    public static bool ShouldToastDm(int authorId, int currentUserId, bool isActiveConvo)
        => authorId != currentUserId && !isActiveConvo;
}
