using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FCG.Contracts;
using FCG.Notifications.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FCG.Notifications.Processing;

public sealed record NotificationRecoveryResult(string Outcome, string? Reason = null, IReadOnlyList<string>? PlannedEffects = null)
{
    public int ExitCode => Outcome == "NotFound" ? 3 : Outcome is "Quarantined" or "LeaseActive" or "WaitingForContact" ? 4 : 0;
}

public sealed class NotificationRecoveryService(NotificationsDbContext db, TimeProvider clock,
    IdentityProjectionHandler identity, PaymentNotificationHandler payment)
{
    public async Task<NotificationRecoveryResult> PreviewConflictAsync(Guid id, CancellationToken ct)
    {
        RequireOwnTransaction(); db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
        var conflict = await db.MessageConflicts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (conflict is null) return new("NotFound");
        if (conflict.Resolved) return new("AlreadyResolved");
        var (message, reason) = ReadStored(conflict);
        return message is null ? new("Quarantined", reason) : await EvaluateAsync(message, ct);
    }

    public async Task<NotificationRecoveryResult> ReplayConflictAsync(Guid id, CancellationToken ct)
    {
        RequireOwnTransaction(); db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await NotificationLocks.AcquireAsync(db, "replay:" + id, ct);
        var conflict = await db.MessageConflicts.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (conflict is null) return new("NotFound");
        NotificationRecoveryResult result;
        if (conflict.Resolved) result = new("AlreadyResolved");
        else
        {
            var (message, reason) = ReadStored(conflict);
            if (message is null) result = new("Quarantined", reason);
            else
            {
                var (decision, applicationReason) = message switch
                {
                    IdentitySnapshot snapshot => (await identity.HandleInTransactionAsync(snapshot, conflict.TransportMessageId, ct), (string?)null),
                    PaymentProcessedEvent processed => await payment.HandleInTransactionAsync(processed, conflict.TransportMessageId, ct),
                    _ => throw new InvalidOperationException("Unsupported validated recovery type.")
                };
                result = new(decision == ProjectionDecision.Conflict ? "Quarantined" : decision.ToString(),
                    decision == ProjectionDecision.Conflict ? applicationReason ?? conflict.Reason : null);
                if (decision != ProjectionDecision.Conflict) { conflict.Resolved = true; conflict.ResolvedAtUtc = clock.GetUtcNow(); }
            }
        }
        db.RecoveryAttempts.Add(new RecoveryAttempt { Id = Guid.NewGuid(), ConflictId = id, AttemptedAtUtc = clock.GetUtcNow(),
            Operation = "ReplayConflict", Outcome = result.Outcome, Reason = result.Reason });
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return result;
    }

    public async Task<NotificationRecoveryResult> ResumeIntentAsync(Guid id, bool apply, CancellationToken ct)
    {
        RequireOwnTransaction(); db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(apply ? IsolationLevel.ReadCommitted : IsolationLevel.RepeatableRead, ct);
        if (!apply) await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
        else await NotificationLocks.AcquireAsync(db, "intent:" + id, ct);
        var intent = await db.NotificationIntents.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (intent is null) return new("NotFound");
        var now = clock.GetUtcNow(); var result = new NotificationRecoveryResult("AlreadyEligible");
        if (intent.State == NotificationState.Sent) result = new("AlreadySent");
        else if (intent.State == NotificationState.Sending)
        {
            if (intent.LeaseToken is null || intent.LeaseExpiresAtUtc is null || intent.LeaseVersion <= 0) result = new("Quarantined", "InvalidLeaseState");
            else if (intent.LeaseExpiresAtUtc > now) result = new("LeaseActive");
            // Keep an expired lease intact: the normal dispatcher must record Abandoned and create the next attempt.
        }
        else if (intent.State == NotificationState.PendingContact)
        {
            var contact = await db.UserContacts.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == intent.UserId, ct);
            if (contact is null) result = new("WaitingForContact");
            else
            {
                result = new("Prepared", PlannedEffects: ["PreparePendingContact"]);
                if (apply) await PendingContactReconciler.PrepareAsync(db, id, now, ct, contact);
            }
        }
        else if (intent.State == NotificationState.Ready && intent.NextAttemptAtUtc > now)
        {
            result = new("Rescheduled", PlannedEffects: ["AdvanceRetry"]);
            if (apply) intent.NextAttemptAtUtc = now;
        }
        if (apply)
        {
            db.RecoveryAttempts.Add(new RecoveryAttempt { Id = Guid.NewGuid(), IntentId = id, AttemptedAtUtc = now,
                Operation = "ResumeIntent", Outcome = result.Outcome, Reason = result.Reason });
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        }
        return result;
    }

    private async Task<NotificationRecoveryResult> EvaluateAsync(object message, CancellationToken ct)
    {
        var effects = new List<string>(); ProjectionDecision decision; string? reason = null;
        if (message is IdentitySnapshot snapshot)
        {
            var contact = await db.UserContacts.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == snapshot.UserId, ct);
            decision = IdentityProjectionPolicy.Evaluate(contact?.Snapshot(), snapshot, ProjectionAudience.Notifications);
            if (decision == ProjectionDecision.Conflict)
                reason = snapshot.EventVersion == contact!.EventVersion ? "DivergentIdentityVersion" : "DeletedIdentityCannotBeRestored";
            else
            {
                if (decision == ProjectionDecision.Apply) effects.Add("ApplyIdentityProjection");
                if (snapshot is UserCreatedEvent && !await db.NotificationIntents.AnyAsync(x => x.Kind == NotificationKind.Welcome && x.BusinessId == snapshot.UserId, ct))
                    effects.Add("CreateWelcomeIntent");
                if (await db.NotificationIntents.AnyAsync(x => x.State == NotificationState.PendingContact && x.UserId == snapshot.UserId, ct)) effects.Add("PreparePendingContactIntents");
            }
        }
        else if (message is PaymentProcessedEvent processed)
        {
            (decision, reason) = await PaymentNotificationHandler.DecideAsync(db, processed, ct);
            if (decision == ProjectionDecision.Apply)
            { effects.Add("RecordPaymentObservation"); if (processed.Status == "Approved") effects.Add("CreatePurchaseConfirmationIntent"); }
        }
        else return new("Quarantined", "UnsupportedMessageType");
        return new(decision == ProjectionDecision.Conflict ? "Quarantined" : decision.ToString(), reason, effects);
    }

    private static (object? Message, string? Reason) ReadStored(MessageConflict conflict)
    {
        if (conflict.Kind == "Identity" && conflict.MessageType is not (nameof(UserCreatedEvent) or nameof(UserStateChangedEvent) or nameof(UserStateSnapshotEvent))
            || conflict.Kind == "Payment" && conflict.MessageType != nameof(PaymentProcessedEvent) || conflict.Kind is not ("Identity" or "Payment"))
            return (null, "UnsupportedMessageType");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(conflict.Payload)));
        if (hash != conflict.PayloadHash) return (null, "InvalidStoredPayload");
        try
        {
            if (conflict.Kind == "Payment")
            {
                var message = JsonSerializer.Deserialize<PaymentProcessedEvent>(conflict.Payload);
                return message is not null && message.OrderId == conflict.BusinessId && conflict.EventId == Guid.Empty && conflict.EventVersion is null
                    ? (message, null) : (null, "InvalidStoredPayload");
            }
            IdentitySnapshot snapshot = conflict.MessageType switch
            {
                nameof(UserCreatedEvent) => IdentityContractJson.Read<UserCreatedEvent>(conflict.Payload),
                nameof(UserStateChangedEvent) => IdentityContractJson.Read<UserStateChangedEvent>(conflict.Payload),
                _ => IdentityContractJson.Read<UserStateSnapshotEvent>(conflict.Payload)
            };
            return snapshot.UserId == conflict.BusinessId && snapshot.EventId == conflict.EventId && snapshot.EventVersion == conflict.EventVersion
                ? (snapshot, null) : (null, "InvalidStoredPayload");
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException) { return (null, "InvalidStoredPayload"); }
    }
    private void RequireOwnTransaction()
    { if (db.Database.CurrentTransaction is not null) throw new InvalidOperationException("Notification recovery owns its transaction."); }
}
