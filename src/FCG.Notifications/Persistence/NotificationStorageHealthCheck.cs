using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FCG.Notifications.Persistence;

public sealed class NotificationStorageHealthCheck(NotificationsDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await db.RecoveryAttempts.AsNoTracking().Take(1).ToArrayAsync(cancellationToken);
            await db.NotificationAttempts.AsNoTracking().Take(1).ToArrayAsync(cancellationToken);
            await db.PaymentObservations.AsNoTracking().Take(1).ToArrayAsync(cancellationToken);
            await db.UserContacts.AsNoTracking().Take(1).ToArrayAsync(cancellationToken);
            await db.MessageConflicts.AsNoTracking().Take(1).ToArrayAsync(cancellationToken);
            await db.NotificationIntents.AsNoTracking().Take(1).ToArrayAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        { return HealthCheckResult.Unhealthy("Notifications storage is unavailable."); }
    }
}
