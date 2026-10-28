namespace FCG.Notifications.Persistence;

public sealed class MessageConflict
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = "";
    public Guid BusinessId { get; set; }
    public Guid EventId { get; set; }
    public Guid? TransportMessageId { get; set; }
    public long? EventVersion { get; set; }
    public string Reason { get; set; } = "";
    public string Payload { get; set; } = "";
    public string PayloadHash { get; set; } = "";
    public DateTimeOffset FirstSeenAtUtc { get; set; }
    public DateTimeOffset LastSeenAtUtc { get; set; }
    public int Observations { get; set; }
    public bool Resolved { get; set; }
    public DateTimeOffset? ResolvedAtUtc { get; set; }
}
