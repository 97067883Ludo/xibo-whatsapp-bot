using Microsoft.EntityFrameworkCore;

namespace Schermpostvak.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Chat> Chats => Set<Chat>();
    public DbSet<MediaItem> MediaItems => Set<MediaItem>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<MediaItem>(e =>
        {
            e.HasIndex(m => m.WaMessageId).IsUnique();
            e.HasIndex(m => m.Sha256);
            e.HasIndex(m => m.Status);
            e.Property(m => m.Type).HasConversion<string>();
            e.Property(m => m.Status).HasConversion<string>();
            e.HasOne(m => m.Chat).WithMany().HasForeignKey(m => m.ChatJid);
        });
    }
}
