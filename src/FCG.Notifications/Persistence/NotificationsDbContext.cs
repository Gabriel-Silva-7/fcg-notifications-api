using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FCG.Notifications.Persistence;

public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    public DbSet<NotificationAttempt> NotificationAttempts => Set<NotificationAttempt>();
    public DbSet<PaymentObservation> PaymentObservations => Set<PaymentObservation>();
    public DbSet<UserContact> UserContacts => Set<UserContact>();
    public DbSet<MessageConflict> MessageConflicts => Set<MessageConflict>();
    public DbSet<NotificationIntent> NotificationIntents => Set<NotificationIntent>();
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
            entity.HasIndex(x => new { x.Kind, x.BusinessId, x.EventId, x.MessageType, x.PayloadHash }).IsUnique();
            entity.Property(x => x.Kind).HasMaxLength(40).IsRequired();
            entity.Property(x => x.MessageType).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Payload).IsRequired();
            entity.Property(x => x.PayloadHash).HasMaxLength(64).IsRequired();
        });
        model.Entity<NotificationAttempt>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.IntentId, x.LeaseVersion }).IsUnique();
            entity.Property(x => x.Result).HasMaxLength(30).IsRequired();
            entity.HasOne<NotificationIntent>().WithMany().HasForeignKey(x => x.IntentId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<PaymentObservation>(entity =>
        {
            entity.HasKey(x => x.OrderId);
            entity.Property(x => x.Price).HasPrecision(18, 2);
            entity.Property(x => x.Status).HasMaxLength(20).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(80);
        });
        model.Entity<NotificationIntent>(entity =>
        {
            entity.ToTable("NotificationIntents", table =>
            {
                table.HasCheckConstraint("CK_NotificationIntents_ReadyContact",
                    "\"State\" = 'PendingContact' OR (\"RecipientName\" IS NOT NULL AND \"RecipientEmail\" IS NOT NULL AND \"ContactEventVersion\" IS NOT NULL AND \"ContactEventVersion\" > 0)");
            });
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.Kind, x.BusinessId }).IsUnique();
            entity.HasIndex(x => new { x.State, x.CreatedAtUtc });
            entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(40);
            entity.Property(x => x.State).HasConversion<string>().HasMaxLength(30);
            entity.Property(x => x.Price).HasPrecision(18, 2);
            entity.Property(x => x.LastFailureCode).HasMaxLength(80);
            entity.Property(x => x.RecipientEmail).HasMaxLength(256);
        });
    }
}

public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<NotificationsDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__Default") ?? "Host=localhost;Database=fcg_notifications;Username=design_time").Options);
}
