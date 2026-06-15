namespace FatGuysSpeak.Shared;

public record RegisterRequest(string Username, string Password, string Email);
public record LoginRequest(string Username, string Password);
public record AuthResponse(string Token, string Username, int UserId, string? AvatarUrl = null);

public record ServerDto(int Id, string Name, string? Description, string OwnerId, int MemberCount, ServerRole MyRole = ServerRole.Member);

public record ServerMemberDto(int UserId, string Username, UserStatus Status, ServerRole Role, DateTime JoinedAt);
public record AuditLogDto(int Id, int ServerId, string ActorUsername, string Action, string? TargetUsername, string? Detail, DateTime CreatedAt);
public record ChannelPermissionDto(int ChannelId, ServerRole MinRoleToRead, ServerRole MinRoleToWrite);
public record SetRoleRequest(ServerRole Role);
public record SetChannelPermissionRequest(ServerRole MinRoleToRead, ServerRole MinRoleToWrite);
public record CreateServerRequest(string Name, string? Description);

public record ChannelDto(int Id, string Name, ChannelType Type, int ServerId, int Position);
public record CreateChannelRequest(string Name, ChannelType Type);

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
    string? AttachmentFileName = null);

public record SendMessageRequest(string Content, MessageSource Source = MessageSource.Text, string? AttachmentUrl = null, int? ReplyToMessageId = null, string? AttachmentFileName = null);
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
    string? AvatarUrl = null);

public record UpdateStatusRequest(UserStatus Status);

public record UserDto(int Id, string Username, UserStatus Status, string? AvatarUrl = null);

public enum ChannelType { Text, Voice }
public enum UserStatus { Offline, Online, Away, DoNotDisturb }

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
    string? AttachmentFileName = null);

public record SendDirectMessageRequest(string? Content, string? AttachmentUrl = null, string? AttachmentFileName = null);
