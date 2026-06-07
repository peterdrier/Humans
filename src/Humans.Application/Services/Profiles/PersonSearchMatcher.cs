using System.Globalization;
using System.Text;
using Humans.Domain.Enums;

namespace Humans.Application.Services.Profiles;

/// <summary>
/// Result of a single person-search match: which field hit, plus optional snippet/email for display
/// and a relevance <paramref name="Score"/> (higher = better) so controllers can rank results by
/// match quality instead of alphabetically. See <see cref="PersonSearchMatcher"/> for the tiers.
/// </summary>
public sealed record PersonSearchMatch(string Field, string? Snippet, string? MatchedEmail, int Score);

/// <summary>
/// Pure, scope-confined matcher for person search. Operates on the cached <see cref="UserInfo"/>
/// read-model so callers (CachingUserService) stay a thin iteration + Guid fast-path over it.
/// Matching is accent- and case-insensitive; name matching is whitespace-token-split.
/// <paramref name="fields"/> IS the authorization model — the matcher never reads a field its
/// scope flag is unset. Board/private fields (notes, IBAN, emergency contact, GDPR health) are
/// never searchable under any flag.
/// </summary>
public static class PersonSearchMatcher
{
    // Relevance tiers (higher = better). Name matches always outrank non-name (bio/city/email)
    // matches, and within a name the order is exact > whole-string-prefix > token-prefix > contains.
    // This is what floats the literal "Ian" above "Adrian"/"Brian" instead of alphabetizing them.
    private const int ScoreExactName = 100;
    private const int ScorePrefixName = 85;
    private const int ScoreTokenPrefixName = 80;
    private const int ScoreContainsName = 60;
    private const int ScoreOtherField = 40;

    /// <summary>Returns the first bucket that matches <paramref name="query"/>, or null if none do.</summary>
    public static PersonSearchMatch? Match(UserInfo user, string query, PersonSearchFields fields)
    {
        if (fields == PersonSearchFields.None || user.Profile is null)
            return null;

        var p = user.Profile;

        // Implicit scope: rejected profiles are never searchable.
        if (p.RejectedAt is not null)
            return null;

        var q = query.Trim();
        if (q.Length == 0)
            return null;

        var tokens = FoldTokens(q);
        if (tokens.Length == 0)
            return null;

        var foldedQuery = Fold(q);

        var includeName = (fields & PersonSearchFields.Name) != PersonSearchFields.None;
        var includeExactName = (fields & PersonSearchFields.ExactName) != PersonSearchFields.None;
        var includeBio = (fields & PersonSearchFields.Bio) != PersonSearchFields.None;
        var includeLegal = (fields & PersonSearchFields.LegalName) != PersonSearchFields.None;
        var includeAdmin = (fields & PersonSearchFields.Admin) != PersonSearchFields.None;

        // ── Exact-name bucket: resolved display name, folded full-string equality (not substring/token). Public. ──
        if (includeExactName && string.Equals(Fold(user.BurnerName), foldedQuery, StringComparison.Ordinal))
            return new PersonSearchMatch("Exact Name", null, null, ScoreExactName);

        // ── Name bucket: resolved display name (BurnerName → DisplayName fallback). Public. ──
        if (includeName && AllTokensIn(user.BurnerName, tokens))
            return new PersonSearchMatch("Name", null, null, ScoreNameTier(Fold(user.BurnerName), foldedQuery));

        // ── Legal name: FirstName/LastName. Admin/coordinator only — never public. ──
        if (includeLegal && AllTokensIn($"{p.FirstName} {p.LastName}", tokens))
            return new PersonSearchMatch(
                "Legal Name", null, null, ScoreNameTier(Fold($"{p.FirstName} {p.LastName}"), foldedQuery));

        // ── Bio bucket: public long-form + CV + AllActiveProfiles ContactFields + publicly-exposed emails. ──
        if (includeBio)
        {
            if (FoldedContains(p.City, foldedQuery))
                return new PersonSearchMatch("City", p.City, null, ScoreOtherField);

            if (FoldedContains(p.ContributionInterests, foldedQuery))
                return new PersonSearchMatch("Interests", Snippet(p.ContributionInterests, q), null, ScoreOtherField);

            if (FoldedContains(p.Bio, foldedQuery))
                return new PersonSearchMatch("Bio", Snippet(p.Bio, q), null, ScoreOtherField);

            if (FoldedContains(p.Pronouns, foldedQuery))
                return new PersonSearchMatch("Pronouns", p.Pronouns, null, ScoreOtherField);

            foreach (var v in p.VolunteerHistory)
            {
                if (FoldedContains(v.EventName, foldedQuery) || FoldedContains(v.Description, foldedQuery))
                    return new PersonSearchMatch("Burner CV", v.EventName, null, ScoreOtherField);
            }

            foreach (var cf in p.ContactFields)
            {
                if (cf.Visibility == ContactFieldVisibility.AllActiveProfiles &&
                    FoldedContains(cf.Value, foldedQuery))
                    return new PersonSearchMatch(DisplayLabel(cf), cf.Value, null, ScoreOtherField);
            }

            foreach (var email in user.UserEmails)
            {
                if (email.IsVerified &&
                    email.Visibility == ContactFieldVisibility.AllActiveProfiles &&
                    FoldedContains(email.Email, foldedQuery))
                    return new PersonSearchMatch("Email", null, email.Email, ScoreOtherField);
            }
        }

        // ── Admin bucket: all verified emails + non-public ContactFields. Admin/board only. ──
        if (includeAdmin)
        {
            foreach (var email in user.AllVerifiedEmails)
            {
                if (FoldedContains(email, foldedQuery))
                    return new PersonSearchMatch("Email", null, email, ScoreOtherField);
            }

            foreach (var cf in p.ContactFields)
            {
                // Public ContactFields are handled in the Bio bucket; Admin covers the remainder.
                if (cf.Visibility == ContactFieldVisibility.AllActiveProfiles)
                    continue;
                if (FoldedContains(cf.Value, foldedQuery))
                    return new PersonSearchMatch(DisplayLabel(cf), cf.Value, cf.Value, ScoreOtherField);
            }
        }

        return null;
    }

    private static bool FoldedContains(string? field, string foldedQuery) =>
        !string.IsNullOrEmpty(field) && Fold(field).Contains(foldedQuery, StringComparison.Ordinal);

    private static string DisplayLabel(ContactFieldInfo cf) =>
        !string.IsNullOrWhiteSpace(cf.CustomLabel) ? cf.CustomLabel! : cf.FieldType.ToString();

    private static string Snippet(string? text, string query, int contextChars = 60)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var index = CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);
        if (index < 0)
            return text.Length <= contextChars * 2 ? text : text[..(contextChars * 2)] + "...";

        var start = Math.Max(0, index - contextChars);
        var end = Math.Min(text.Length, index + query.Length + contextChars);
        var snippet = text[start..end];
        if (start > 0) snippet = "..." + snippet;
        if (end < text.Length) snippet += "...";
        return snippet;
    }

    /// <summary>Folds a string for accent-/case-insensitive comparison: lowercase + NFD + strip combining marks.</summary>
    internal static string Fold(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var nfd = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(nfd.Length);
        foreach (var ch in nfd)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>Folds the query and splits on whitespace into non-empty tokens.</summary>
    private static string[] FoldTokens(string query) =>
        Fold(query).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Ranks an already-matched name: exact &gt; whole-string-prefix &gt; token-prefix &gt; contains.
    /// Both arguments are pre-folded. The caller has already confirmed a match, so the floor is
    /// <see cref="ScoreContainsName"/> (covers token-substring/multi-token hits that aren't a prefix).
    /// </summary>
    private static int ScoreNameTier(string foldedName, string foldedQuery)
    {
        if (string.Equals(foldedName, foldedQuery, StringComparison.Ordinal))
            return ScoreExactName;
        if (foldedName.StartsWith(foldedQuery, StringComparison.Ordinal))
            return ScorePrefixName;
        foreach (var token in foldedName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith(foldedQuery, StringComparison.Ordinal))
                return ScoreTokenPrefixName;
        }

        return ScoreContainsName;
    }

    /// <summary>True when every token is a substring of the folded haystack (order-independent).</summary>
    private static bool AllTokensIn(string? haystack, string[] foldedTokens)
    {
        if (string.IsNullOrEmpty(haystack))
            return false;

        var folded = Fold(haystack);
        foreach (var token in foldedTokens)
        {
            if (!folded.Contains(token, StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}
