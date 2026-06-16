namespace Humans.Domain.Helpers;

/// <summary>
/// Normalizes email addresses for comparison to prevent provider-level aliases from causing mismatches.
/// Google treats @googlemail.com and @gmail.com as identical, but string comparison doesn't.
/// </summary>
public static class EmailNormalization
{
    /// <summary>
    /// Normalizes an email for comparison only (never for storage).
    /// Lowercases and maps @googlemail.com ↔ @gmail.com so they compare equal.
    /// </summary>
    public static string NormalizeForComparison(string email)
    {
        if (string.IsNullOrEmpty(email))
            return email;

        var lower = email.ToLowerInvariant();

        if (lower.EndsWith("@googlemail.com", StringComparison.Ordinal))
            return string.Concat(lower.AsSpan(0, lower.Length - "@googlemail.com".Length), "@gmail.com");

        return lower;
    }

    /// <summary>
    /// Canonicalizes a Gmail address to the form Google itself resolves it to:
    /// builds on <see cref="NormalizeForComparison"/> (lowercase, @googlemail.com → @gmail.com)
    /// then strips the "+tag" sub-address from the local part
    /// (peter+travel@gmail.com → peter@gmail.com). Non-Gmail addresses are returned
    /// unchanged — plus-addressing is not a universal alias outside Gmail.
    /// Used at the Google Workspace boundary, where the raw "+tag" form is rejected
    /// (HTTP 404) and would otherwise be re-added on every sync pass.
    /// </summary>
    public static string CanonicalizeGmail(string email)
    {
        if (string.IsNullOrEmpty(email))
            return email;

        const string gmailSuffix = "@gmail.com";
        var normalized = NormalizeForComparison(email);
        if (!normalized.EndsWith(gmailSuffix, StringComparison.Ordinal))
            return email;

        var localPart = normalized[..^gmailSuffix.Length];
        var plusIndex = localPart.IndexOf('+');
        if (plusIndex < 0)
            return normalized;

        return string.Concat(localPart.AsSpan(0, plusIndex), gmailSuffix);
    }

    /// <summary>
    /// Compares two email addresses for equivalence, treating @googlemail.com and @gmail.com as the same domain.
    /// </summary>
    public static bool EmailsMatch(string? a, string? b)
    {
        if (a is null || b is null) return string.Equals(a, b, StringComparison.Ordinal);
        return string.Equals(NormalizeForComparison(a), NormalizeForComparison(b), StringComparison.Ordinal);
    }
}

/// <summary>
/// IEqualityComparer that normalizes googlemail↔gmail before comparing.
/// </summary>
public sealed class NormalizingEmailComparer : IEqualityComparer<string>
{
    public static readonly NormalizingEmailComparer Instance = new();

    public bool Equals(string? x, string? y) => EmailNormalization.EmailsMatch(x, y);

    public int GetHashCode(string obj) => StringComparer.Ordinal.GetHashCode(EmailNormalization.NormalizeForComparison(obj));
}
