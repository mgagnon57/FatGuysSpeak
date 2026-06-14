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

    public List<ServerMember> ServerMemberships { get; set; } = [];
    public List<Message> Messages { get; set; } = [];
}

public class GuildServer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int OwnerId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? InviteCode { get; set; }

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
}

public class Channel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public ChannelType Type { get; set; }
    public int ServerId { get; set; }
    public GuildServer Server { get; set; } = null!;
    public int Position { get; set; }

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

