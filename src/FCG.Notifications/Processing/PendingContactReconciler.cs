using FCG.Notifications.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FCG.Notifications.Processing;

public sealed class PendingContactReconciler(NotificationsDbContext db, TimeProvider clock)
{
    public async Task<int> ReconcileAsync(CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var ids = await db.NotificationIntents.AsNoTracking().Where(x => x.State == NotificationState.PendingContact
                && db.UserContacts.Any(contact => contact.UserId == x.UserId))
            .OrderBy(x => x.CreatedAtUtc).Select(x => x.Id).Take(100).ToArrayAsync(cancellationToken);
        var count = 0;
        foreach (var id in ids)
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            if (await PrepareAsync(db, id, clock.GetUtcNow(), cancellationToken)) count++;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        return count;
    }

    internal static async Task<bool> PrepareAsync(NotificationsDbContext db, Guid id, DateTimeOffset now, CancellationToken cancellationToken, UserContact? projectedContact = null)
    {
        await NotificationLocks.AcquireAsync(db, "intent:" + id, cancellationToken);
        // A concurrent reconciler may have committed while this transaction waited for the lock.
        var intent = await db.NotificationIntents.SingleAsync(x => x.Id == id, cancellationToken);
        await db.Entry(intent).ReloadAsync(cancellationToken);
        if (intent.State != NotificationState.PendingContact) return false;
        var contact = projectedContact ?? await db.UserContacts.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == intent.UserId, cancellationToken);
        if (contact is null) return false;
        intent.Prepare(contact, now);
        return true;
    }
}
