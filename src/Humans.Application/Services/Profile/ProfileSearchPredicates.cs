namespace Humans.Application.Services.Profile;

/// <summary>
/// Reusable <see cref="FullProfile"/> predicates for the consolidated
/// <c>IProfileService.SearchProfilesAsync</c> surface. Keeps the per-caller
/// match conditions in one place so controllers stay slim and the
/// "broad-match across bio / city / interests / pronouns / CV" definition
/// can change in exactly one location.
/// </summary>
public static class ProfileSearchPredicates
{
    /// <summary>
    /// Display name + burner name match. Used by the typeahead picker
    /// (<c>scope=name</c>) and recipient-lookup callers.
    /// </summary>
    public static Func<FullProfile, bool> ByName(string query) =>
        p =>
            p.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            (p.BurnerName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// Broad match across all indexed profile fields plus CV entries.
    /// Equivalent to the legacy <c>SearchHumansAsync</c> match set.
    /// </summary>
    public static Func<FullProfile, bool> Broad(string query) =>
        p =>
            p.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            (p.BurnerName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (p.City?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (p.Bio?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (p.ContributionInterests?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (p.Pronouns?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
            p.CVEntries.Any(v =>
                v.EventName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (v.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));

    /// <summary>
    /// Display name + primary email match. Used by the team-admin user picker
    /// (members are looked up by either their visible name or their email).
    /// </summary>
    public static Func<FullProfile, bool> ByNameOrPrimaryEmail(string query) =>
        p =>
            p.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            (p.PrimaryEmail?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
}
