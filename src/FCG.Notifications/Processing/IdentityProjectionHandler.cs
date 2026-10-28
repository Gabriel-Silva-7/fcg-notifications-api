using System.Security.Cryptography;
using System.Text;
using FCG.Contracts;
using FCG.Notifications.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FCG.Notifications.Processing;

public sealed class IdentityProjectionHandler(NotificationsDbContext db, TimeProvider clock, ILogger<IdentityProjectionHandler> logger)
{
    public async Task<ProjectionDecision> HandleAsync(IdentitySnapshot incoming, Guid? transportId, CancellationToken cancellationToken)
    {
        IdentitySnapshotValidation.Validate(incoming);
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await NotificationLocks.AcquireAsync(db, "user:" + incoming.UserId, cancellationToken);
        var contact = await db.UserContacts.SingleOrDefaultAsync(x => x.UserId == incoming.UserId, cancellationToken);
        var decision = IdentityProjectionPolicy.Evaluate(contact?.Snapshot(), incoming, ProjectionAudience.Notifications);
        if (decision == ProjectionDecision.Apply)
        {
            if (contact is null) { contact = new UserContact(); db.UserContacts.Add(contact); }
            contact.Apply(incoming);
        }
        else if (decision == ProjectionDecision.Conflict)
        {
            var payload = IdentityContractJson.Write(incoming);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
            var conflict = await db.MessageConflicts.SingleOrDefaultAsync(x => x.Kind == "Identity"
                && x.BusinessId == incoming.UserId && x.EventId == incoming.EventId && x.PayloadHash == hash, cancellationToken);
            if (conflict is null)
            {
                conflict = new MessageConflict
                {
                    Id = Guid.NewGuid(), Kind = "Identity", BusinessId = incoming.UserId, EventId = incoming.EventId,
                    TransportMessageId = transportId, EventVersion = incoming.EventVersion,
                    Reason = incoming.EventVersion == contact!.EventVersion
                        ? "DivergentIdentityVersion" : "DeletedIdentityCannotBeRestored",
                    Payload = payload, PayloadHash = hash, FirstSeenAtUtc = clock.GetUtcNow()
                };
                db.MessageConflicts.Add(conflict);
            }
            conflict.Observations++;
            conflict.LastSeenAtUtc = clock.GetUtcNow();
            logger.LogWarning("IdentityProjectionConflict {UserId} {EventId} {EventVersion}", incoming.UserId, incoming.EventId, incoming.EventVersion);
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return decision;
    }
}

public static class NotificationLocks
{
    public static Task AcquireAsync(NotificationsDbContext db, string key, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken);
}
