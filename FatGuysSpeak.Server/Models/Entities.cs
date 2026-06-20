using FatGuysSpeak.Shared;

namespace FatGuysSpeak.Server.Models;

public class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public UserStatus Status { get; set; } = UserStatus.Offline;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? AvatarUrl { get; set; }
    public string? Bio { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public long TotalOnlineSeconds { get; set; }

    public List<ServerMember> ServerMemberships { get; set; } = [];
    public List<Message> Messages { get; set; } = [];
}

public class ExternalLogin
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Provider { get; set; } = "";
    public string ProviderUserId { get; set; } = "";
    public string Email { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class GuildServer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int OwnerId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? InviteCode { get; set; }
    public string? VanityCode { get; set; }
    public byte[]? IconData { get; set; }
    public string? IconMimeType { get; set; }
    public ServerRole MinRoleToMentionEveryone { get; set; } = ServerRole.Admin;

    public List<Channel> Channels { get; set; } = [];
    public List<ServerMember> Members { get; set; } = [];
}

public class ServerMember
{
    public int ServerId { get; set; }
    public GuildServer Server { get; set; } = null!;
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public ServerRole Role { get; set; } = ServerRole.Member;
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
    public DateTime? MutedUntil { get; set; }
}

public class TempBan
{
    public int Id { get; set; }
    public int ServerId { get; set; }
    public int UserId { get; set; }
    public int ActorId { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class WordFilter
{
    public int Id { get; set; }
    public int ServerId { get; set; }
    public string Pattern { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public WordFilterSeverity Severity { get; set; } = WordFilterSeverity.Delete;
    public bool CaseSensitive { get; set; } = false;
}

public class Category
{
    public int Id { get; set; }
    public int ServerId { get; set; }
    public GuildServer Server { get; set; } = null!;
    public string Name { get; set; } = "";
    public int Position { get; set; }
}

public class Channel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public ChannelType Type { get; set; }
    public int ServerId { get; set; }
    public GuildServer Server { get; set; } = null!;
    public int Position { get; set; }
    public int? CategoryId { get; set; }
    public Category? Category { get; set; }
    public int SlowmodeSeconds { get; set; }
    public string? Topic { get; set; }
    public bool IsNsfw { get; set; }
    // The server's default channel: cannot be deleted (can be renamed), and is where
    // users are bumped when a channel is deleted or they're removed from one.
    public bool IsDefault { get; set; }

    public List<Message> Messages { get; set; } = [];
}

public class Message
{
    public int Id { get; set; }
    public string Content { get; set; } = "";
    public int AuthorId { get; set; }
    public User Author { get; set; } = null!;
    public int ChannelId { get; set; }
    public Channel Channel { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public MessageSource Source { get; set; } = MessageSource.Text;
    public string? AttachmentUrl { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime? EditedAt { get; set; }
    public int? ReplyToId { get; set; }
    public Message? ReplyTo { get; set; }
    public string? AttachmentFileName { get; set; }
    public int? ThreadId { get; set; }
    public Message? Thread { get; set; }
}

public class DirectConversation
{
    public int Id { get; set; }
    public int User1Id { get; set; }  // always smaller userId
    public User User1 { get; set; } = null!;
    public int User2Id { get; set; }  // always larger userId
    public User User2 { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<DirectMessage> Messages { get; set; } = [];
}

public class DirectMessage
{
    public int Id { get; set; }
    public int ConversationId { get; set; }
    public DirectConversation Conversation { get; set; } = null!;
    public int AuthorId { get; set; }
    public User Author { get; set; } = null!;
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
    public string? AttachmentUrl { get; set; }
    public string? AttachmentFileName { get; set; }
}

public class PinnedMessage
{
    public int Id { get; set; }
    public int MessageId { get; set; }
    public Message Message { get; set; } = null!;
    public int ChannelId { get; set; }
    public Channel Channel { get; set; } = null!;
    public int PinnedById { get; set; }
    public User PinnedBy { get; set; } = null!;
    public DateTime PinnedAt { get; set; } = DateTime.UtcNow;
}

public class PinnedDirectMessage
{
    public int Id { get; set; }
    public int DirectMessageId { get; set; }
    public DirectMessage DirectMessage { get; set; } = null!;
    public int ConversationId { get; set; }
    public DirectConversation Conversation { get; set; } = null!;
    public int PinnedById { get; set; }
    public User PinnedBy { get; set; } = null!;
    public DateTime PinnedAt { get; set; } = DateTime.UtcNow;
}

public class DirectConversationRead
{
    public int ConversationId { get; set; }
    public int UserId { get; set; }
    public DateTime LastReadAt { get; set; } = DateTime.UtcNow;
}

public class UserBlock
{
    public int BlockerId { get; set; }
    public User Blocker { get; set; } = null!;
    public int BlockedId { get; set; }
    public User Blocked { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// One PorkChop recap per channel per completed (UTC) day, generated lazily on first view and cached.
public class DailyChatSummary
{
    public int Id { get; set; }
    public int ChannelId { get; set; }
    public DateTime Date { get; set; }            // UTC calendar day (date component) being summarized
    public string Summary { get; set; } = "";
    public int MessageCount { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}

public class AuditLog
{
    public int Id { get; set; }
    public int ServerId { get; set; }
    public int ActorId { get; set; }
    public string ActorUsername { get; set; } = "";
    public string Action { get; set; } = "";
    public int? TargetId { get; set; }
    public string? TargetUsername { get; set; }
    public string? Detail { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ChannelPermission
{
    public int ChannelId { get; set; }
    public ServerRole MinRoleToRead { get; set; } = ServerRole.Member;
    public ServerRole MinRoleToWrite { get; set; } = ServerRole.Member;
}

public class MessageReaction
{
    public int Id { get; set; }
    public int MessageId { get; set; }
    public Message Message { get; set; } = null!;
    public int UserId { get; set; }
    public string Username { get; set; } = "";
    public string Emoji { get; set; } = "";
}

public class UserChannelNotif
{
    public int UserId { get; set; }
    public int ChannelId { get; set; }
    public NotifLevel Level { get; set; }
}

// Monotonic counters that must never go backwards even when rows are deleted.
// Used to hand out channel ids that are never reused (SQLite recycles rowids otherwise).
public class AppSequence
{
    public string Name { get; set; } = "";
    public long Value { get; set; }
}

public class UserServerNotif
{
    public int UserId { get; set; }
    public int ServerId { get; set; }
    public NotifLevel Level { get; set; }
}

public class PasswordResetToken
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public string Token { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsUsed { get; set; }
}

public class UserSession
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public string TokenHash { get; set; } = "";
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt { get; set; }
}

public class UserWarning
{
    public int Id { get; set; }
    public int ServerId { get; set; }
    public int UserId { get; set; }
    public int ActorId { get; set; }
    public string ActorUsername { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Webhook
{
    public int Id { get; set; }
    public int ServerId { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Events { get; set; } = "message,member_join,member_leave";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int CreatedById { get; set; }
}

public class GroupConversation
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int CreatedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<GroupConversationMember> Members { get; set; } = [];
    public List<GroupMessage> Messages { get; set; } = [];
}

public class GroupConversationMember
{
    public int GroupConversationId { get; set; }
    public GroupConversation GroupConversation { get; set; } = null!;
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
}

public class GroupMessage
{
    public int Id { get; set; }
    public int GroupConversationId { get; set; }
    public GroupConversation GroupConversation { get; set; } = null!;
    public int AuthorId { get; set; }
    public User Author { get; set; } = null!;
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
}

