using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FCG.Contracts;
using FCG.Notifications.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FCG.Notifications.Processing;

public sealed class PaymentNotificationHandler(NotificationsDbContext db, TimeProvider clock, ILogger<PaymentNotificationHandler> logger)
{
    public async Task<ProjectionDecision> HandleAsync(PaymentProcessedEvent incoming, Guid? transportId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var result = await HandleInTransactionAsync(incoming, transportId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result.Decision;
    }

    internal async Task<(ProjectionDecision Decision, string? Reason)> HandleInTransactionAsync(PaymentProcessedEvent incoming, Guid? transportId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Notification application requires its owner's transaction.");
        await NotificationLocks.AcquireAsync(db, "order:" + incoming.OrderId, cancellationToken);
        var (decision, conflictReason) = await DecideAsync(db, incoming, cancellationToken);
        if (decision == ProjectionDecision.Apply)
        {
            db.PaymentObservations.Add(PaymentObservation.From(incoming));
            if (incoming.Status == "Approved")
            {
                var intent = new NotificationIntent
                { Id = Guid.NewGuid(), Kind = NotificationKind.PurchaseConfirmation, BusinessId = incoming.OrderId,
                  UserId = incoming.UserId, GameId = incoming.GameId, Price = incoming.Price, CreatedAtUtc = clock.GetUtcNow() };
                // One consistent local snapshot; absence stays recoverable even if contact commits just afterward.
                var contact = await db.UserContacts.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == incoming.UserId, cancellationToken);
                if (contact is not null) intent.Prepare(contact, clock.GetUtcNow());
                db.NotificationIntents.Add(intent);
            }
        }
        else if (decision == ProjectionDecision.Conflict)
        {
            var payload = JsonSerializer.Serialize(incoming);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
            var conflict = await db.MessageConflicts.SingleOrDefaultAsync(x => x.Kind == "Payment" && x.BusinessId == incoming.OrderId
                && x.EventId == Guid.Empty && x.MessageType == nameof(PaymentProcessedEvent) && x.PayloadHash == hash, cancellationToken);
            if (conflict is null)
            {
                conflict = new MessageConflict { Id = Guid.NewGuid(), Kind = "Payment", BusinessId = incoming.OrderId,
                    MessageType = nameof(PaymentProcessedEvent), TransportMessageId = transportId,
                    Reason = conflictReason!, Payload = payload, PayloadHash = hash, FirstSeenAtUtc = clock.GetUtcNow() };
                db.MessageConflicts.Add(conflict);
            }
            conflict.Observations++; conflict.LastSeenAtUtc = clock.GetUtcNow();
            logger.LogWarning("PaymentObservationConflict {OrderId} {MessageId}", incoming.OrderId, transportId);
        }
        await db.SaveChangesAsync(cancellationToken);
        return (decision, conflictReason);
    }

    internal static async Task<(ProjectionDecision Decision, string? Reason)> DecideAsync(NotificationsDbContext db, PaymentProcessedEvent incoming, CancellationToken ct)
    {
        var current = await db.PaymentObservations.AsNoTracking().SingleOrDefaultAsync(x => x.OrderId == incoming.OrderId, ct);
        if (current is null)
        {
            if (await db.NotificationIntents.AsNoTracking().AnyAsync(x => x.Kind == NotificationKind.PurchaseConfirmation && x.BusinessId == incoming.OrderId, ct))
                return (ProjectionDecision.Conflict, "ExistingIntentWithoutPaymentObservation");
            return (ProjectionDecision.Apply, null);
        }
        return current.Snapshot() == incoming ? (ProjectionDecision.Ignore, null) : (ProjectionDecision.Conflict, "DivergentPaymentResult");
    }
}
