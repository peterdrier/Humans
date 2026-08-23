namespace Humans.Base.Helpers;

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
    /// Comparison-only Gmail identity key: builds on <see cref="CanonicalizeGmail"/>
    /// (lowercase, googlemail→gmail, strip "+tag") and additionally strips dots
    /// from the local part, since Gmail ignores them too
    /// (first.last@gmail.com and firstlast@gmail.com are the same account).
    /// Unlike <see cref="CanonicalizeGmail"/>, never use this for a Drive API
    /// target address — dotted addresses are accepted as-is by Drive, only
    /// "+tag" is rejected (nobodies-collective/Humans#945). This is for
    /// matching a Google-reported address against stored <c>user_emails</c>
    /// rows that may carry either alias form (nobodies-collective/Humans#1101).
    /// </summary>
    public static string CanonicalizeGmailAlias(string email)
    {
        var canonical = CanonicalizeGmail(email);
        if (string.IsNullOrEmpty(canonical))
            return canonical;

        const string gmailSuffix = "@gmail.com";
        if (!canonical.EndsWith(gmailSuffix, StringComparison.Ordinal))
            return canonical;

        var localPart = canonical[..^gmailSuffix.Length].Replace(".", string.Empty, StringComparison.Ordinal);
        return string.Concat(localPart, gmailSuffix);
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

/// <summary>
/// IEqualityComparer over <see cref="EmailNormalization.CanonicalizeGmailAlias"/>: folds
/// googlemail↔gmail, "+tag" and dots, so a stored alias row and the canonical address
/// Google reports compare equal. Use wherever a Google-reported address is keyed against
/// stored <c>user_emails</c> data (nobodies-collective/Humans#1101) —
/// <see cref="NormalizingEmailComparer"/> folds only case and the domain, so an
/// alias-only match hashes to a different bucket and is silently dropped.
/// </summary>
public sealed class GmailAliasEmailComparer : IEqualityComparer<string>
{
    public static readonly GmailAliasEmailComparer Instance = new();

    public bool Equals(string? x, string? y) =>
        x is null || y is null
            ? string.Equals(x, y, StringComparison.Ordinal)
            : string.Equals(
                EmailNormalization.CanonicalizeGmailAlias(x),
                EmailNormalization.CanonicalizeGmailAlias(y),
                StringComparison.OrdinalIgnoreCase);

    // CanonicalizeGmailAlias leaves non-Gmail addresses at their original casing,
    // so the hash must be case-insensitive to agree with Equals.
    public int GetHashCode(string obj) =>
        StringComparer.OrdinalIgnoreCase.GetHashCode(EmailNormalization.CanonicalizeGmailAlias(obj));
}
