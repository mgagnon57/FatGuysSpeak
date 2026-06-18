namespace FatGuysSpeak.Shared;

public record RegisterRequest(string Username, string Password, string Email);
public record LoginRequest(string Username, string Password);
public record AuthResponse(string Token, string Username, int UserId, string? AvatarUrl = null);
public record GoogleAuthRequest(string IdToken);
public record GoogleCodeExchangeRequest(string Code, string CodeVerifier, string RedirectUri);
public record GoogleConfigResponse(string ClientId);

public record ServerDto(int Id, string Name, string? Description, string OwnerId, int MemberCount, ServerRole MyRole = ServerRole.Member, bool HasIcon = false, NotifLevel? UserNotifLevel = null);

public record ServerMemberDto(int UserId, string Username, UserStatus Status, ServerRole Role, DateTime JoinedAt);
public record AuditLogDto(int Id, int ServerId, string ActorUsername, string Action, string? TargetUsername, string? Detail, DateTime CreatedAt);
public record UserProfileAdminDto(
    int Id, string Username, string Email, string? AvatarUrl, string? Bio,
    string Status, bool InVoice,
    DateTime CreatedAt, string Role, DateTime? MutedUntil, DateTime? TempBanExpiresAt,
    DateTime? LastLoginAt, string? LastLoginIp, string? LastLoginUserAgent,
    DateTime? LastSeenAt, int ActiveSessionCount,
    int MessageCount, string? TopChannel, long TotalOnlineSeconds);
public record ChannelPermissionDto(int ChannelId, ServerRole MinRoleToRead, ServerRole MinRoleToWrite);
public record SetRoleRequest(ServerRole Role);
public record SetChannelPermissionRequest(ServerRole MinRoleToRead, ServerRole MinRoleToWrite);
public record CreateServerRequest(string Name, string? Description);

public record ChannelDto(int Id, string Name, ChannelType Type, int ServerId, int Position, int? CategoryId = null, int SlowmodeSeconds = 0, NotifLevel? UserNotifLevel = null, string? Topic = null, bool IsNsfw = false, bool IsDefault = false);
public record SetNotifLevelRequest(NotifLevel Level);
public record CreateChannelRequest(string Name, ChannelType Type);
public record SetSlowmodeRequest(int Seconds);
public record MuteUserRequest(int Seconds);
public record TempBanRequest(int Seconds);
public record WordFilterDto(int Id, string Pattern, DateTime CreatedAt, WordFilterSeverity Severity = WordFilterSeverity.Delete, bool CaseSensitive = false);
public enum WordFilterSeverity { Log, Delete, Mute }
public record AddWordFilterRequest(string Pattern, WordFilterSeverity Severity = WordFilterSeverity.Delete, bool CaseSensitive = false);

public record CategoryDto(int Id, int ServerId, string Name, int Position);
public record CreateCategoryRequest(string Name);
public record RenameCategoryRequest(string Name);
public record SetChannelCategoryRequest(int? CategoryId);

public record ReactionDto(string Emoji, int Count, bool IsOwn);
public record ReactionsUpdatedDto(int MessageId, List<ReactionDto> Reactions);

public record MessageDto(
    int Id,
    string Content,
    string AuthorUsername,
    int AuthorId,
    DateTime CreatedAt,
    int ChannelId,
    MessageSource Source = MessageSource.Text,
    string? AttachmentUrl = null,
    bool IsDeleted = false,
    DateTime? EditedAt = null,
    List<ReactionDto>? Reactions = null,
    string? AuthorAvatarUrl = null,
    int? ReplyToId = null,
    string? ReplyToUsername = null,
    string? ReplyPreview = null,
    string? AttachmentFileName = null,
    bool IsPinned = false,
    int? ThreadId = null,
    int ReplyCount = 0);

public record SendMessageRequest(string Content, MessageSource Source = MessageSource.Text, string? AttachmentUrl = null, int? ReplyToMessageId = null, string? AttachmentFileName = null, int? ThreadId = null);
public record EditMessageRequest(string Content);
public record AttachmentDto(string Url, string? OriginalFileName = null, string? ContentType = null);

public enum MessageSource { Text, Voice, Stream, AI }

public record UpdateStatusDto(string Current, string? Latest, bool UpdateAvailable, string? ReleaseUrl);
public enum ServerRole { Member, Moderator, Admin }

public record UserProfileDto(
    int Id,
    string Username,
    UserStatus Status,
    DateTime CreatedAt,
    ServerRole? Role,
    DateTime? JoinedAt,
    bool IsCurrentUser,
    string? AvatarUrl = null,
    string? Bio = null,
    DateTime? LastSeenAt = null);

public record UpdateStatusRequest(UserStatus Status);
public record UpdateBioRequest(string? Bio);
public record UpdateUsernameRequest(string Username);
public record BlockedUserDto(int UserId, string Username, DateTime BlockedAt);

public record UserDto(int Id, string Username, UserStatus Status, string? AvatarUrl = null);

public enum ChannelType { Text, Voice }
public enum UserStatus { Offline, Online, Away, DoNotDisturb }
public enum NotifLevel { All = 0, OnlyMentions = 1, Muted = 2 }

public record VoiceStateDto(int UserId, string Username, int? ChannelId, bool Muted, bool Deafened);
public record JoinVoiceRequest(int ChannelId);

public record LinkPreviewDto(string Url, string? Title, string? Description, string? ImageUrl, string? SiteName);

public record ServerInviteDto(string Code, int ServerId, string ServerName, int MemberCount);

public record DirectConversationDto(
    int Id,
    int OtherUserId,
    string OtherUsername,
    string? OtherAvatarUrl,
    string? LastMessagePreview,
    DateTime? LastMessageAt);

public record DirectMessageDto(
    int Id,
    string Content,
    string AuthorUsername,
    int AuthorId,
    DateTime CreatedAt,
    int ConversationId,
    bool IsDeleted = false,
    string? AttachmentUrl = null,
    string? AuthorAvatarUrl = null,
    string? AttachmentFileName = null,
    bool IsPinned = false);

public record SendDirectMessageRequest(string? Content, string? AttachmentUrl = null, string? AttachmentFileName = null);

public record DmReadStateDto(int ConversationId, DateTime MyLastReadAt, DateTime? OtherUserLastReadAt);

// Password reset
public record ForgotPasswordRequest(string Email);
public record ResetPasswordRequest(string Token, string NewPassword);

// Sessions
public record UserSessionDto(int Id, string? IpAddress, DateTime CreatedAt, DateTime LastSeenAt, bool IsCurrent);

// Warnings
public record UserWarningDto(int Id, int UserId, string ActorUsername, string Reason, DateTime CreatedAt);
public record AddWarningRequest(string Reason);

// Webhooks
public record WebhookDto(int Id, string Name, string Url, string Events, DateTime CreatedAt);
public record CreateWebhookRequest(string Name, string Url, string Events);

// Group DMs
public record GroupConversationDto(int Id, string Name, List<UserDto> Members, string? LastMessagePreview, DateTime? LastMessageAt, DateTime CreatedAt);
public record GroupMessageDto(int Id, string Content, string AuthorUsername, int AuthorId, DateTime CreatedAt, int GroupConversationId, bool IsDeleted = false, string? AuthorAvatarUrl = null);
public record CreateGroupConversationRequest(string Name, List<int> MemberUserIds);
public record SendGroupMessageRequest(string Content);

// Channel topic / NSFW
public record SetChannelTopicRequest(string? Topic, bool? IsNsfw = null);

// Vanity invite
public record SetVanityCodeRequest(string Code);

// Mention gating
public record SetMentionRoleRequest(ServerRole MinRole);

// Automod
public record AutomodActionDto(string Type, string Detail, DateTime OccurredAt);

// Admin message log
public record AdminMessageDto(
    int Id, string Content, int AuthorId, string Author, string Channel,
    string Server, string Source, DateTime CreatedAt, bool IsDeleted);

public record MessageFilterDto(
    string? Author = null, string? Channel = null, int? ServerId = null,
    string? Source = null, string? Keyword = null,
    DateTime? From = null, DateTime? To = null);

public record BulkRestoreRequest(int[]? Ids = null, MessageFilterDto? Filter = null);
public record BulkDeleteRequest(int[]? Ids = null, MessageFilterDto? Filter = null, string Mode = "soft");
public record BulkActionResult(int Affected, int[] ChannelIds);

// Admin server list (filter dropdown)
public record AdminServerDto(int Id, string Name);

// Remote control
public enum RemoteInputKind { Move, Down, Up, Wheel, KeyDown, KeyUp }

public record RemoteInputDto(
    RemoteInputKind Kind,
    double X = 0, double Y = 0,   // normalized 0..1 (Move/Down/Up/Wheel)
    int Button = 0,               // 0=left, 1=right, 2=middle (Down/Up)
    int Delta = 0,                // wheel notches * 120 (Wheel)
    int KeyCode = 0);             // Win32 virtual-key code (KeyDown/KeyUp)
