using FCG.Notifications.Persistence;
using FCG.Notifications.Processing;
using Microsoft.EntityFrameworkCore;

namespace FCG.Notifications.Delivery;

public sealed class NotificationDispatcher(NotificationsDbContext db, TimeProvider clock, DispatcherOptions options,
    INotificationSink sink, ILogger<NotificationDispatcher> logger)
{
    public async Task<NotificationDelivery?> ClaimAsync(CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear(); var now = clock.GetUtcNow();
        var candidate = await db.NotificationIntents.AsNoTracking().Where(x =>
            x.State == NotificationState.Ready && (x.NextAttemptAtUtc == null || x.NextAttemptAtUtc <= now)
            || x.State == NotificationState.Sending && x.LeaseExpiresAtUtc <= now)
            .OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(cancellationToken);
        if (candidate is null) return null;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await NotificationLocks.AcquireAsync(db, "intent:" + candidate, cancellationToken);
        var intent = await db.NotificationIntents.SingleAsync(x => x.Id == candidate, cancellationToken);
        now = clock.GetUtcNow();
        if (!(intent.State == NotificationState.Ready && (intent.NextAttemptAtUtc is null || intent.NextAttemptAtUtc <= now)
            || intent.State == NotificationState.Sending && intent.LeaseExpiresAtUtc <= now)) return null;
        if (intent.LeaseToken is { } previous)
        {
            var abandoned = await db.NotificationAttempts.SingleAsync(x => x.Id == previous, cancellationToken);
            abandoned.Result = "Abandoned"; abandoned.CompletedAtUtc = now;
        }
        intent.State = NotificationState.Sending; intent.LeaseToken = Guid.NewGuid();
        intent.LeaseVersion++; intent.Attempts++; intent.LeaseExpiresAtUtc = now.AddSeconds(options.LeaseSeconds);
        intent.NextAttemptAtUtc = null; intent.LastFailureCode = null;
        db.NotificationAttempts.Add(new NotificationAttempt { Id = intent.LeaseToken.Value, IntentId = intent.Id,
            LeaseVersion = intent.LeaseVersion, ClaimedAtUtc = now, ExpiresAtUtc = intent.LeaseExpiresAtUtc.Value });
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return new(intent.Id, intent.LeaseToken.Value, intent.LeaseVersion, intent.Kind, intent.BusinessId,
            intent.UserId, intent.GameId, intent.Price, SensitiveDataMasker.MaskEmail(intent.RecipientEmail));
    }

    public Task<bool> CompleteAsync(NotificationDelivery delivery, CancellationToken cancellationToken) =>
        FinishAsync(delivery, sent: true, cancellationToken);

    private async Task<bool> FinishAsync(NotificationDelivery delivery, bool sent, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await NotificationLocks.AcquireAsync(db, "intent:" + delivery.IntentId, cancellationToken);
        var intent = await db.NotificationIntents.SingleAsync(x => x.Id == delivery.IntentId, cancellationToken);
        var now = clock.GetUtcNow();
        if (intent.State != NotificationState.Sending || intent.LeaseToken != delivery.AttemptId
            || intent.LeaseVersion != delivery.LeaseVersion || intent.LeaseExpiresAtUtc <= now) return false;
        var attempt = await db.NotificationAttempts.SingleAsync(x => x.Id == delivery.AttemptId, cancellationToken);
        attempt.Result = sent ? "Sent" : "Failed"; attempt.CompletedAtUtc = now;
        intent.State = sent ? NotificationState.Sent : NotificationState.Ready;
        intent.SentAtUtc = sent ? now : null;
        intent.LastFailureCode = sent ? null : "ConsoleEmissionFailed";
        intent.NextAttemptAtUtc = sent ? null : now.AddSeconds(options.BackoffSeconds);
        intent.LeaseToken = null; intent.LeaseExpiresAtUtc = null;
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return true;
    }

    public async Task<bool> DispatchNextAsync(CancellationToken cancellationToken)
    {
        var delivery = await ClaimAsync(cancellationToken);
        if (delivery is null) return false;
        try { await sink.EmitAsync(delivery, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Do not record untrusted exception text: sinks may include contact or infrastructure secrets.
            logger.LogWarning("NotificationEmissionFailed IntentId={IntentId} AttemptId={AttemptId}", delivery.IntentId, delivery.AttemptId);
            await FinishAsync(delivery, sent: false, cancellationToken); return true;
        }
        // Failure here leaves Sending recoverable by lease. The console may already contain the effect.
        await CompleteAsync(delivery, cancellationToken); return true;
    }
}
