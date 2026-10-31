using System.Globalization;
using System.Resources;
using System.Text;
using Humans.Users.Contracts;

namespace Humans.Users.Services;

/// <summary>
/// Which field hit, an optional snippet/email, and a relevance <paramref name="Score"/> (higher is
/// better). See <see cref="PersonSearchMatcher"/> for the tiers.
/// </summary>
internal sealed record PersonSearchMatch(string Field, string? Snippet, string? MatchedEmail, int Score);

/// <summary>
/// Accent- and case-insensitive; names match per token. <c>fields</c> IS the authorization model:
/// the matcher never reads a field whose flag is unset, and Board/private fields (notes, IBAN,
/// emergency contact, health) are never searchable.
/// </summary>
internal static class PersonSearchMatcher
{
    private static readonly ResourceManager Resources = new(typeof(UsersResource));

    // Name matches outrank everything else, and within a name exact > prefix > token-prefix >
    // contains, so "Ian" floats above "Adrian" instead of alphabetizing.
    private const int ScoreExactName = 100;
    private const int ScorePrefixName = 85;
    private const int ScoreTokenPrefixName = 80;
    private const int ScoreContainsName = 60;
    private const int ScoreOtherField = 40;

    /// <summary>Returns the first bucket that matches <paramref name="query"/>, or null if none do.</summary>
    public static PersonSearchMatch? Match(UserInfo user, string query, PersonSearchFields fields)
    {
        // Implicit scope: no-profile and rejected users are never searchable.
        if (fields == PersonSearchFields.None || !user.IsSearchable)
            return null;

        var p = user.Profile!;

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
            return new PersonSearchMatch(Label("Users_Search_ExactName"), null, null, ScoreExactName);

        // ── Name bucket: the already-resolved UserInfo.BurnerName. Public. ──
        if (includeName && AllTokensIn(user.BurnerName, tokens))
            return new PersonSearchMatch(Label("Users_Search_Name"), null, null, ScoreNameTier(Fold(user.BurnerName), foldedQuery));

        // ── Legal name: FirstName/LastName. Admin/coordinator only — never public. ──
        if (includeLegal && AllTokensIn($"{p.FirstName} {p.LastName}", tokens))
            return new PersonSearchMatch(
                Label("Users_Profile_LegalName"), null, null, ScoreNameTier(Fold($"{p.FirstName} {p.LastName}"), foldedQuery));

        // ── Bio bucket: public long-form + CV + AllActiveProfiles ContactFields + publicly-exposed emails. ──
        if (includeBio)
        {
            if (FoldedContains(p.City, foldedQuery))
                return new PersonSearchMatch(Label("Users_ProfileEdit_City"), p.City, null, ScoreOtherField);

            if (FoldedContains(p.ContributionInterests, foldedQuery))
                return new PersonSearchMatch(Label("Users_Search_Interests"), Snippet(p.ContributionInterests, q), null, ScoreOtherField);

            if (FoldedContains(p.Bio, foldedQuery))
                return new PersonSearchMatch(Label("Users_Profile_Bio"), Snippet(p.Bio, q), null, ScoreOtherField);

            if (FoldedContains(p.Pronouns, foldedQuery))
                return new PersonSearchMatch(Label("Users_Search_Pronouns"), p.Pronouns, null, ScoreOtherField);

            var historyMatch = p.VolunteerHistory
                .Where(v => FoldedContains(v.EventName, foldedQuery) || FoldedContains(v.Description, foldedQuery))
                .MaxBy(v => v.Date);
            if (historyMatch is not null)
                return new PersonSearchMatch(Label("Users_Profile_BurnerCV"), historyMatch.EventName, null, ScoreOtherField);

            var publicContactMatch = p.ContactFields
                .Where(cf => cf.Visibility == ContactFieldVisibility.AllActiveProfiles && FoldedContains(cf.Value, foldedQuery))
                .MinBy(cf => cf.DisplayOrder);
            if (publicContactMatch is not null)
                return new PersonSearchMatch(DisplayLabel(publicContactMatch), publicContactMatch.Value, null, ScoreOtherField);

            foreach (var email in user.UserEmails)
            {
                if (email.IsVerified &&
                    email.Visibility == ContactFieldVisibility.AllActiveProfiles &&
                    FoldedContains(email.Email, foldedQuery))
                    return new PersonSearchMatch(Label("Enum_ContactFieldType_Email"), null, email.Email, ScoreOtherField);
            }
        }

        // ── Admin bucket: all verified emails + non-public ContactFields. Admin/board only. ──
        if (includeAdmin)
        {
            foreach (var email in user.AllVerifiedEmails)
            {
                if (FoldedContains(email, foldedQuery))
                    return new PersonSearchMatch(Label("Enum_ContactFieldType_Email"), null, email, ScoreOtherField);
            }

            // Public ContactFields are handled in the Bio bucket; Admin covers the remainder.
            var privateContactMatch = p.ContactFields
                .Where(cf => cf.Visibility != ContactFieldVisibility.AllActiveProfiles && FoldedContains(cf.Value, foldedQuery))
                .MinBy(cf => cf.DisplayOrder);
            if (privateContactMatch is not null)
                return new PersonSearchMatch(DisplayLabel(privateContactMatch), privateContactMatch.Value, privateContactMatch.Value, ScoreOtherField);
        }

        return null;
    }

    private static bool FoldedContains(string? field, string foldedQuery) =>
        !string.IsNullOrEmpty(field) && Fold(field).Contains(foldedQuery, StringComparison.Ordinal);

    private static string DisplayLabel(ContactFieldInfo cf) =>
        !string.IsNullOrWhiteSpace(cf.CustomLabel)
            ? cf.CustomLabel!
            : Resources.GetString($"Enum_ContactFieldType_{cf.FieldType}", CultureInfo.CurrentUICulture)
                ?? cf.FieldType.ToString();

    private static string Label(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture)!;

    private static string Snippet(string? text, string query, int contextChars = 60)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var index = CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);
        var start = index < 0 ? 0 : Math.Max(0, index - contextChars);
        var end = Math.Min(text.Length, index < 0 ? contextChars * 2 : index + query.Length + contextChars);
        // A context edge may fall between the UTF-16 halves of one character.
        if (start > 0 && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1]))
            start--;
        if (end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end]))
            end++;
        var snippet = text[start..end];
        if (start > 0) snippet = "..." + snippet;
        if (end < text.Length) snippet += "...";
        return snippet;
    }

    /// <summary>Lowercase + NFD + strip combining marks.</summary>
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

    private static string[] FoldTokens(string query) =>
        Fold(query).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Ranks an already-matched, pre-folded name. The floor is <see cref="ScoreContainsName"/>,
    /// which covers token-substring and multi-token hits that aren't a prefix.
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

    /// <summary>Order-independent.</summary>
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
