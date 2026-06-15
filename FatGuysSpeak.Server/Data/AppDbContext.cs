using FatGuysSpeak.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Server.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<GuildServer> Servers => Set<GuildServer>();
    public DbSet<ServerMember> ServerMembers => Set<ServerMember>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageReaction> MessageReactions => Set<MessageReaction>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ChannelPermission> ChannelPermissions => Set<ChannelPermission>();
    public DbSet<DirectConversation> DirectConversations => Set<DirectConversation>();
    public DbSet<DirectMessage> DirectMessages => Set<DirectMessage>();
    public DbSet<DirectConversationRead> DirectConversationReads => Set<DirectConversationRead>();
    public DbSet<PinnedMessage> PinnedMessages => Set<PinnedMessage>();
    public DbSet<PinnedDirectMessage> PinnedDirectMessages => Set<PinnedDirectMessage>();
    public DbSet<UserBlock> UserBlocks => Set<UserBlock>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ServerMember>()
            .HasKey(sm => new { sm.ServerId, sm.UserId });

        b.Entity<User>()
            .HasIndex(u => u.Username).IsUnique();
        b.Entity<User>()
            .HasIndex(u => u.Email).IsUnique();

        b.Entity<MessageReaction>()
            .HasIndex(r => new { r.MessageId, r.UserId, r.Emoji }).IsUnique();

        b.Entity<ChannelPermission>()
            .HasKey(cp => cp.ChannelId);

        b.Entity<Message>()
            .HasOne(m => m.ReplyTo)
            .WithMany()
            .HasForeignKey(m => m.ReplyToId)
            .OnDelete(DeleteBehavior.SetNull);

        b.Entity<DirectConversation>()
            .HasIndex(dc => new { dc.User1Id, dc.User2Id }).IsUnique();

        b.Entity<DirectMessage>()
            .HasOne(dm => dm.Conversation)
            .WithMany(dc => dc.Messages)
            .HasForeignKey(dm => dm.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<DirectConversationRead>()
            .HasKey(r => new { r.ConversationId, r.UserId });

        b.Entity<PinnedMessage>()
            .HasIndex(p => p.MessageId).IsUnique();

        b.Entity<PinnedDirectMessage>()
            .HasIndex(p => p.DirectMessageId).IsUnique();

        b.Entity<UserBlock>()
            .HasKey(ub => new { ub.BlockerId, ub.BlockedId });
    }
}
