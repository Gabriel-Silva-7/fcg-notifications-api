namespace FCG.Contracts;

public enum ProjectionDecision { Apply, Ignore, Conflict }
public enum ProjectionAudience { Catalog, Notifications }

/// <summary>Pure comparison only. Consumers must enforce this atomically in their own database.</summary>
public static class IdentityProjectionPolicy
{
    public static ProjectionDecision Evaluate(IdentitySnapshot? current, IdentitySnapshot incoming, ProjectionAudience audience)
    {
        IdentitySnapshotValidation.Validate(incoming);
        if (!Enum.IsDefined(audience)) throw new ArgumentOutOfRangeException(nameof(audience));
        if (current is null) return ProjectionDecision.Apply;
        IdentitySnapshotValidation.Validate(current);
        if (current.UserId != incoming.UserId) throw new ArgumentException("Cannot compare different users.", nameof(incoming));
        if (incoming.EventVersion < current.EventVersion) return ProjectionDecision.Ignore;
        if (incoming.EventVersion == current.EventVersion)
        {
            var equivalent = incoming.State == current.State && incoming.Role == current.Role
                && (audience == ProjectionAudience.Catalog || (incoming.Name == current.Name && incoming.Email == current.Email));
            return equivalent ? ProjectionDecision.Ignore : ProjectionDecision.Conflict;
        }
        if (current.State == IdentityState.Deleted && incoming.State != IdentityState.Deleted) return ProjectionDecision.Conflict;
        return ProjectionDecision.Apply;
    }
}
