using System.Text.RegularExpressions;

namespace FCG.Contracts;

public static class IdentitySnapshotValidation
{
    public static void Validate(IdentitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.EventId == Guid.Empty || snapshot.UserId == Guid.Empty || snapshot.EventVersion <= 0)
            throw new ArgumentException("Identity identifiers and logical version must be positive and nonempty.", nameof(snapshot));
        if (!Enum.IsDefined(snapshot.Role) || !Enum.IsDefined(snapshot.State))
            throw new ArgumentException("Unknown identity role or lifecycle state.", nameof(snapshot));
        if (string.IsNullOrWhiteSpace(snapshot.Name) || snapshot.Name != snapshot.Name.Trim())
            throw new ArgumentException("A normalized name is required.", nameof(snapshot));
        if (string.IsNullOrWhiteSpace(snapshot.Email) || snapshot.Email != snapshot.Email.Trim().ToLowerInvariant()
            || !Regex.IsMatch(snapshot.Email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            throw new ArgumentException("A normalized email is required.", nameof(snapshot));
        if (!IsUtcMicroseconds(snapshot.OccurredAtUtc) || !IsUtcMicroseconds(snapshot.CreatedAtUtc)
            || snapshot.CreatedAtUtc > snapshot.OccurredAtUtc)
            throw new ArgumentException("Identity times must be UTC microseconds with creation no later than occurrence.", nameof(snapshot));
        if (snapshot is UserCreatedEvent && (snapshot.EventVersion != 1 || snapshot.State != IdentityState.Active
            || snapshot.CreatedAtUtc != snapshot.OccurredAtUtc))
            throw new ArgumentException("Registration requires the initial active version and matching creation time.", nameof(snapshot));
        if (snapshot is UserStateChangedEvent && snapshot.EventVersion < 2)
            throw new ArgumentException("A state change follows the initial version.", nameof(snapshot));
    }

    private static bool IsUtcMicroseconds(DateTimeOffset value) =>
        value != default && value.Offset == TimeSpan.Zero && value.Ticks % 10 == 0;
}
