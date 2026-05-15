using Humans.Application.DTOs;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Services.Profiles;

/// <summary>
/// Pure in-memory matcher for the person-search bit-flag API. Called by
/// both the base <see cref="Humans.Application.Services.Profiles.ProfileService"/>
/// (DB-backed snapshot) and the caching decorator (dict-backed snapshot)
/// with the same output shape, so search semantics live in exactly one
/// place.
///
/// <para><b>Scope (implicit):</b> rows are filtered to "not rejected, not
/// deleted" — the only population anyone is searching. Suspended profiles
/// are <i>included</i> for admin callers but excluded for public-only
/// callers; see <see cref="Match"/> for the rule.</para>
///
/// <para><b>Emergency contact data is never read.</b> The
/// <see cref="Profile.EmergencyContactName"/> /
/// <see cref="Profile.EmergencyContactPhone"/> fields are skipped by every
/// branch regardless of which bits are passed.</para>
/// </summary>
public static class PersonSearchMatcher
{
    /// <summary>
    /// Runs the matcher across <paramref name="snapshot"/>. Returns matching
    /// rows in unspecified order — the caller (controller) sorts and
    /// applies its own display ordering per
    /// <c>memory/architecture/display-sort-in-controllers.md</c>. The
    /// service-side <paramref name="limit"/> is enforced as a safety cap on
    /// raw row count (so a very broad query doesn't fan out to thousands of
    /// objects); presentation may reduce further but cannot expand past it.
    /// </summary>
    /// <param name="snapshot">Eligible profiles (already pre-filtered to
    /// "not rejected"). The matcher applies the suspended-vs-public-only
    /// rule itself based on <paramref name="fields"/>.</param>
    /// <param name="contactFieldsByProfileId">All ContactField rows keyed by
    /// owning <see cref="Profile.Id"/>. Empty/absent values mean the profile
    /// has no contact fields. May be <c>null</c> when neither
    /// <see cref="PersonSearchFields.Bio"/> nor
    /// <see cref="PersonSearchFields.Admin"/> is set.</param>
    public static IReadOnlyList<HumanSearchResult> Match(
        IEnumerable<FullProfile> snapshot,
        string query,
        PersonSearchFields fields,
        IReadOnlyDictionary<Guid, IReadOnlyList<ContactField>>? contactFieldsByProfileId,
        int limit)
    {
        if (fields == PersonSearchFields.None || string.IsNullOrWhiteSpace(query) || limit <= 0)
        {
            return Array.Empty<HumanSearchResult>();
        }

        var includeAdmin = (fields & PersonSearchFields.Admin) != PersonSearchFields.None;

        // Admin-only exact-UserId lookup. Lets an admin paste a UserId from
        // logs / audit trails / URLs and jump straight to that human. Public
        // callers fall through to text matching so they can't enumerate IDs.
        if (includeAdmin && Guid.TryParse(query, out var idGuid))
        {
            var byId = snapshot.FirstOrDefault(p => p.UserId == idGuid);
            if (byId is null) return Array.Empty<HumanSearchResult>();

            var idBurnerName = string.IsNullOrWhiteSpace(byId.BurnerName)
                ? byId.DisplayName
                : byId.BurnerName!;

            return new[] { new HumanSearchResult(
                UserId: byId.UserId,
                ProfileId: byId.ProfileId,
                BurnerName: idBurnerName,
                ProfilePictureUrl: byId.ProfilePictureUrl,
                MatchField: "User ID",
                MatchSnippet: null,
                MatchedEmail: null) };
        }

        var results = new List<HumanSearchResult>();

        foreach (var p in snapshot)
        {
            // Public-only callers never see suspended humans. Admin callers
            // do, because admin search is the primary tool for finding a
            // suspended person to lift suspension etc.
            if (!includeAdmin && p.IsSuspended) continue;

            // Public-only: only approved profiles surface. Admin: pre-approval
            // / consent-pending profiles are valid search targets.
            if (!includeAdmin && !p.IsApproved) continue;

            var match = TryMatch(p, query, fields, contactFieldsByProfileId);
            if (match is null) continue;

            var burnerName = string.IsNullOrWhiteSpace(p.BurnerName)
                ? p.DisplayName
                : p.BurnerName!;

            results.Add(new HumanSearchResult(
                UserId: p.UserId,
                ProfileId: p.ProfileId,
                BurnerName: burnerName,
                ProfilePictureUrl: p.ProfilePictureUrl,
                MatchField: match.Value.Field,
                MatchSnippet: match.Value.Snippet,
                MatchedEmail: match.Value.MatchedEmail));

            if (results.Count >= limit) break;
        }

        return results;
    }

    private static (string Field, string? Snippet, string? MatchedEmail)?
        TryMatch(
            FullProfile p,
            string query,
            PersonSearchFields fields,
            IReadOnlyDictionary<Guid, IReadOnlyList<ContactField>>? contactFieldsByProfileId)
    {
        var includeName = (fields & PersonSearchFields.Name) != PersonSearchFields.None;
        var includeBio = (fields & PersonSearchFields.Bio) != PersonSearchFields.None;
        var includeAdmin = (fields & PersonSearchFields.Admin) != PersonSearchFields.None;

        // ── Name bucket ─────────────────────────────────────────────
        if (includeName)
        {
            // BurnerName only — see memory/architecture/burnername-is-the-display-name.md.
            if (!string.IsNullOrEmpty(p.BurnerName) &&
                p.BurnerName.Contains(query, StringComparison.OrdinalIgnoreCase))
                return ("Name", null, null);
        }

        // ── Bio bucket (public long-form + short fields + public ContactFields) ──
        if (includeBio)
        {
            if (p.City?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                return ("City", p.City, null);

            if (p.ContributionInterests?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                return ("Interests", GetSnippet(p.ContributionInterests, query), null);

            if (p.Bio?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                return ("Bio", GetSnippet(p.Bio, query), null);

            if (p.Pronouns?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                return ("Pronouns", p.Pronouns, null);

            foreach (var v in p.CVEntries)
            {
                if (v.EventName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    v.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                    return ("Burner CV", v.EventName, null);
            }

            if (contactFieldsByProfileId is not null &&
                contactFieldsByProfileId.TryGetValue(p.ProfileId, out var allFields))
            {
                foreach (var cf in allFields)
                {
                    if (cf.Visibility != ContactFieldVisibility.AllActiveProfiles) continue;
                    if (cf.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
                        return (cf.DisplayLabel, cf.Value, null);
                }
            }
        }

        // ── Admin bucket (verified emails + non-public ContactFields) ───────────
        if (includeAdmin)
        {
            foreach (var email in p.VerifiedEmails)
            {
                if (email.Contains(query, StringComparison.OrdinalIgnoreCase))
                    return ("Email", null, email);
            }

            if (contactFieldsByProfileId is not null &&
                contactFieldsByProfileId.TryGetValue(p.ProfileId, out var allFields))
            {
                foreach (var cf in allFields)
                {
                    // Public ContactFields were already handled above (when
                    // the Bio bit was on). Admin bucket covers the remainder.
                    if (cf.Visibility == ContactFieldVisibility.AllActiveProfiles) continue;
                    if (cf.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
                        return (cf.DisplayLabel, cf.Value, cf.Value);
                }
            }
        }

        return null;
    }

    private static string GetSnippet(string text, string query, int contextChars = 60)
    {
        var index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return text.Length <= contextChars * 2 ? text : text[..(contextChars * 2)] + "...";

        var start = Math.Max(0, index - contextChars);
        var end = Math.Min(text.Length, index + query.Length + contextChars);
        var snippet = text[start..end];
        if (start > 0) snippet = "..." + snippet;
        if (end < text.Length) snippet += "...";
        return snippet;
    }
}
