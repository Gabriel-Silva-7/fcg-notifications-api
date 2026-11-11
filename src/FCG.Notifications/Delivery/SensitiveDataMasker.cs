using System.Net.Mail;

namespace FCG.Notifications.Delivery;

// Preserves the inherited mask and email validation without referencing another service's assembly.
public static class SensitiveDataMasker
{
    public const string FullyMasked = "***";
    public static string MaskEmail(string? email)
    {
        if (email is null) return FullyMasked;
        var value = email.Trim().ToLowerInvariant();
        if (value.Length > 256 || value.Any(char.IsControl) || !MailAddress.TryCreate(value, out var parsed) || parsed.Address != value)
            return FullyMasked;
        var separator = value.IndexOf('@', StringComparison.Ordinal);
        if (separator < 1) return FullyMasked;
        return (separator == 1 ? FullyMasked : value[0] + FullyMasked) + value[separator..];
    }
}
