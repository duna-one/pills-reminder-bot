using Microsoft.EntityFrameworkCore;
using PillsReminderBot.Domain;

namespace PillsReminderBot.Persistence;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<Reminder> Reminders => Set<Reminder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserProfile>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TelegramUserId).IsUnique();

            e.Property(x => x.TimeZoneId).HasMaxLength(64);
        });

        modelBuilder.Entity<Reminder>(e =>
        {
            e.HasKey(x => x.Id);

            e.HasIndex(x => x.TelegramUserId);
            e.HasIndex(x => x.NextFireAtUtc);

            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.ActiveCycleId).HasMaxLength(64);
        });
    }
}


