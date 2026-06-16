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
    public DbSet<TempBan> TempBans => Set<TempBan>();
    public DbSet<WordFilter> WordFilters => Set<WordFilter>();
    public DbSet<UserChannelNotif> UserChannelNotifs => Set<UserChannelNotif>();
    public DbSet<UserServerNotif> UserServerNotifs => Set<UserServerNotif>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<UserWarning> UserWarnings => Set<UserWarning>();
    public DbSet<Webhook> Webhooks => Set<Webhook>();
    public DbSet<GroupConversation> GroupConversations => Set<GroupConversation>();
    public DbSet<GroupConversationMember> GroupConversationMembers => Set<GroupConversationMember>();
    public DbSet<GroupMessage> GroupMessages => Set<GroupMessage>();
    public DbSet<AppSequence> AppSequences => Set<AppSequence>();

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

        b.Entity<UserChannelNotif>()
            .HasKey(n => new { n.UserId, n.ChannelId });

        b.Entity<UserServerNotif>()
            .HasKey(n => new { n.UserId, n.ServerId });

        b.Entity<GroupConversationMember>()
            .HasKey(m => new { m.GroupConversationId, m.UserId });

        b.Entity<GroupMessage>()
            .HasOne(gm => gm.GroupConversation)
            .WithMany(gc => gc.Messages)
            .HasForeignKey(gm => gm.GroupConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<GuildServer>()
            .HasIndex(s => s.VanityCode).IsUnique();

        b.Entity<AppSequence>()
            .HasKey(s => s.Name);
    }
}
