using System.Globalization;
using System.Text;
using Humans.Domain.Enums;

namespace Humans.Application.Services.Profiles;

/// <summary>Result of a single person-search match: which field hit, plus optional snippet/email for display.</summary>
public sealed record PersonSearchMatch(string Field, string? Snippet, string? MatchedEmail);

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
        var includeBio = (fields & PersonSearchFields.Bio) != PersonSearchFields.None;
        var includeLegal = (fields & PersonSearchFields.LegalName) != PersonSearchFields.None;
        var includeAdmin = (fields & PersonSearchFields.Admin) != PersonSearchFields.None;

        // ── Name bucket: resolved display name (BurnerName → DisplayName fallback). Public. ──
        if (includeName && AllTokensIn(user.BurnerName, tokens))
            return new PersonSearchMatch("Name", null, null);

        // ── Legal name: FirstName/LastName. Admin/coordinator only — never public. ──
        if (includeLegal && AllTokensIn($"{p.FirstName} {p.LastName}", tokens))
            return new PersonSearchMatch("Legal Name", null, null);

        // ── Bio bucket: public long-form + CV + AllActiveProfiles ContactFields + publicly-exposed emails. ──
        if (includeBio)
        {
            if (FoldedContains(p.City, foldedQuery))
                return new PersonSearchMatch("City", p.City, null);

            if (FoldedContains(p.ContributionInterests, foldedQuery))
                return new PersonSearchMatch("Interests", Snippet(p.ContributionInterests, q), null);

            if (FoldedContains(p.Bio, foldedQuery))
                return new PersonSearchMatch("Bio", Snippet(p.Bio, q), null);

            if (FoldedContains(p.Pronouns, foldedQuery))
                return new PersonSearchMatch("Pronouns", p.Pronouns, null);

            foreach (var v in p.VolunteerHistory)
            {
                if (FoldedContains(v.EventName, foldedQuery) || FoldedContains(v.Description, foldedQuery))
                    return new PersonSearchMatch("Burner CV", v.EventName, null);
            }

            foreach (var cf in p.ContactFields)
            {
                if (cf.Visibility == ContactFieldVisibility.AllActiveProfiles &&
                    FoldedContains(cf.Value, foldedQuery))
                    return new PersonSearchMatch(DisplayLabel(cf), cf.Value, null);
            }

            foreach (var email in user.UserEmails)
            {
                if (email.IsVerified &&
                    email.Visibility == ContactFieldVisibility.AllActiveProfiles &&
                    FoldedContains(email.Email, foldedQuery))
                    return new PersonSearchMatch("Email", null, email.Email);
            }
        }

        // ── Admin bucket: all verified emails + non-public ContactFields. Admin/board only. ──
        if (includeAdmin)
        {
            foreach (var email in user.AllVerifiedEmails)
            {
                if (FoldedContains(email, foldedQuery))
                    return new PersonSearchMatch("Email", null, email);
            }

            foreach (var cf in p.ContactFields)
            {
                // Public ContactFields are handled in the Bio bucket; Admin covers the remainder.
                if (cf.Visibility == ContactFieldVisibility.AllActiveProfiles)
                    continue;
                if (FoldedContains(cf.Value, foldedQuery))
                    return new PersonSearchMatch(DisplayLabel(cf), cf.Value, cf.Value);
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
