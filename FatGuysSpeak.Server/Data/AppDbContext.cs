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

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ServerMember>()
            .HasKey(sm => new { sm.ServerId, sm.UserId });

        b.Entity<User>()
            .HasIndex(u => u.Username).IsUnique();
        b.Entity<User>()
            .HasIndex(u => u.Email).IsUnique();

    }
}
