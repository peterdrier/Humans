namespace Humans.Domain.Helpers;

/// <summary>
/// Canonicalizes email addresses to prevent provider-level aliases from causing mismatches.
/// Google treats @googlemail.com and @gmail.com as identical, but string comparison doesn't.
/// </summary>
public static class EmailNormalization
{
    /// <summary>
    /// Replaces @googlemail.com with @gmail.com (case-insensitive domain match).
    /// Returns the input unchanged for all other domains.
    /// </summary>
    public static string Canonicalize(string email)
    {
        if (string.IsNullOrEmpty(email))
            return email;

        if (email.EndsWith("@googlemail.com", StringComparison.OrdinalIgnoreCase))
            return string.Concat(email.AsSpan(0, email.Length - "@googlemail.com".Length), "@gmail.com");

        return email;
    }
}
