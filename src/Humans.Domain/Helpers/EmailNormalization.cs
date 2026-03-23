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
