namespace FCG.Notifications.Persistence;

public sealed class RecoveryAttempt
{
    public Guid Id { get; set; }
    public Guid? ConflictId { get; set; }
    public Guid? IntentId { get; set; }
    public DateTimeOffset AttemptedAtUtc { get; set; }
    public string Operation { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string? Reason { get; set; }
}
