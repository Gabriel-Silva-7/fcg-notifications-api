using FCG.Contracts;

namespace FCG.Notifications.Persistence;

public sealed class UserContact
{
    public Guid UserId { get; set; }
    public Guid EventId { get; set; }
    public long EventVersion { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public IdentityRole Role { get; set; }
    public IdentityState State { get; set; }

    public UserStateSnapshotEvent Snapshot() => new()
    {
        UserId = UserId, EventId = EventId, EventVersion = EventVersion,
        OccurredAtUtc = OccurredAtUtc, CreatedAtUtc = CreatedAtUtc,
        Name = Name, Email = Email, Role = Role, State = State
    };

    public void Apply(IdentitySnapshot snapshot)
    {
        UserId = snapshot.UserId; EventId = snapshot.EventId; EventVersion = snapshot.EventVersion;
        OccurredAtUtc = snapshot.OccurredAtUtc; CreatedAtUtc = snapshot.CreatedAtUtc;
        Name = snapshot.Name; Email = snapshot.Email; Role = snapshot.Role; State = snapshot.State;
    }
}
