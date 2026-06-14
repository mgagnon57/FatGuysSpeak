using FatGuysSpeak.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FatGuysSpeak.Server.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<GuildServer> Servers => Set<GuildServer>();
    public DbSet<ServerMember> ServerMembers => Set<ServerMember>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageReaction> MessageReactions => Set<MessageReaction>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ChannelPermission> ChannelPermissions => Set<ChannelPermission>();

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
    }
}
