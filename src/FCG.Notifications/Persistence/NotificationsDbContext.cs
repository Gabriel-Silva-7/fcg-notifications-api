using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FCG.Notifications.Persistence;

public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    public DbSet<UserContact> UserContacts => Set<UserContact>();
    public DbSet<MessageConflict> MessageConflicts => Set<MessageConflict>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<UserContact>(entity =>
        {
            entity.ToTable("UserContacts", table => table.HasCheckConstraint("CK_UserContacts_Version", "\"EventVersion\" > 0"));
            entity.HasKey(x => x.UserId);
            // The identity wire contract requires a normalized name, without an HTTP DTO length cap.
            entity.Property(x => x.Name).IsRequired();
            entity.Property(x => x.Email).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
        });
        model.Entity<MessageConflict>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.Kind, x.BusinessId, x.EventId, x.PayloadHash }).IsUnique();
            entity.Property(x => x.Kind).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Payload).IsRequired();
            entity.Property(x => x.PayloadHash).HasMaxLength(64).IsRequired();
        });
    }
}

public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<NotificationsDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__Default") ?? "Host=localhost;Database=fcg_notifications;Username=design_time").Options);
}
