namespace FCG.Notifications.Persistence;

public enum NotificationKind { Welcome, PurchaseConfirmation }
public enum NotificationState { PendingContact, Ready, Sending, Sent }

public sealed class NotificationIntent
{
    public Guid Id { get; set; }
    public NotificationKind Kind { get; set; }
    public Guid BusinessId { get; set; }
    public Guid UserId { get; set; }
    public Guid? GameId { get; set; }
    public decimal? Price { get; set; }
    public NotificationState State { get; set; }
    public string? RecipientName { get; set; }
    public string? RecipientEmail { get; set; }
    public long? ContactEventVersion { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? LeaseToken { get; set; }
    public long LeaseVersion { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAtUtc { get; set; }
    public DateTimeOffset? SentAtUtc { get; set; }
    public string? LastFailureCode { get; set; }
    public DateTimeOffset? PreparedAtUtc { get; set; }

    public void Prepare(UserContact contact, DateTimeOffset now)
    {
        if (State != NotificationState.PendingContact || contact.UserId != UserId)
            throw new InvalidOperationException("Only a pending intent can select its own user's contact.");
        RecipientName = contact.Name; RecipientEmail = contact.Email; ContactEventVersion = contact.EventVersion;
        PreparedAtUtc = now; State = NotificationState.Ready;
    }

    public static NotificationIntent Welcome(UserContact contact, DateTimeOffset now)
    {
        var intent = new NotificationIntent
        { Id = Guid.NewGuid(), Kind = NotificationKind.Welcome, BusinessId = contact.UserId, UserId = contact.UserId, CreatedAtUtc = now };
        intent.Prepare(contact, now);
        return intent;
    }
}
