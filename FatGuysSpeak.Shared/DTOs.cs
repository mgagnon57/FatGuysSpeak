namespace FatGuysSpeak.Shared;

public record RegisterRequest(string Username, string Password, string Email);
public record LoginRequest(string Username, string Password);
public record AuthResponse(string Token, string Username, int UserId, string? AvatarUrl = null);
public record GoogleAuthRequest(string IdToken);

public record ServerDto(int Id, string Name, string? Description, string OwnerId, int MemberCount, ServerRole MyRole = ServerRole.Member, bool HasIcon = false, NotifLevel? UserNotifLevel = null);

public record ServerMemberDto(int UserId, string Username, UserStatus Status, ServerRole Role, DateTime JoinedAt);
public record AuditLogDto(int Id, int ServerId, string ActorUsername, string Action, string? TargetUsername, string? Detail, DateTime CreatedAt);
public record ChannelPermissionDto(int ChannelId, ServerRole MinRoleToRead, ServerRole MinRoleToWrite);
public record SetRoleRequest(ServerRole Role);
public record SetChannelPermissionRequest(ServerRole MinRoleToRead, ServerRole MinRoleToWrite);
public record CreateServerRequest(string Name, string? Description);

public record ChannelDto(int Id, string Name, ChannelType Type, int ServerId, int Position, int? CategoryId = null, int SlowmodeSeconds = 0, NotifLevel? UserNotifLevel = null, string? Topic = null, bool IsNsfw = false);
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
