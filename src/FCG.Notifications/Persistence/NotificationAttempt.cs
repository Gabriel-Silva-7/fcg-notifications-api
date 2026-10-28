namespace FCG.Notifications.Persistence;

public sealed class NotificationAttempt
{
    public Guid Id { get; set; }
    public Guid IntentId { get; set; }
    public long LeaseVersion { get; set; }
    public DateTimeOffset ClaimedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string Result { get; set; } = "Claimed";
}
