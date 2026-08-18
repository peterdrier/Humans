namespace Humans.Base.Models;

/// <summary>
/// One projected person-search row, as rendered by the canonical
/// <c>_HumanSearchResults</c> partial. Bound by four Shell pages
/// (<c>/Profile/Search</c>, <c>/Users/Admin</c>, the widget gallery, the team
/// admin picker) and by the Search section's <c>/Search</c> page.
/// </summary>
/// <remarks>
/// Lives in <c>Humans.Base</c> rather than Shell for the reason
/// <c>HumanLookupSearchResult</c> and <c>AssigneeOption</c> do: a section cannot
/// name a <c>Humans.Web</c> type, it carries no section vocabulary, and
/// duplicating it would fork the shape the shared partial exists to keep
/// (G5-SECTION-TEMPLATE.md step 6). The partial itself moved to <c>Humans.Users</c>
/// at G5 lane 4b-i (nobodies-collective/Humans#866); this model stayed exactly so
/// Shell and Search can keep naming it.
/// </remarks>
public class HumanSearchResultViewModel
{
    public Guid UserId { get; set; }
    public string BurnerName { get; set; } = string.Empty;
    public string? ProfilePictureUrl { get; set; }
    public string? MatchField { get; set; }
    public string? MatchSnippet { get; set; }

    /// <summary>
    /// Verified email address that matched, when the controller passed the
    /// <c>PersonSearchFields.Admin</c> bit. Always null on public surfaces.
    /// </summary>
    public string? MatchedEmail { get; set; }

    // Set by the AdminList controller to surface partition status, primary
    // email, and admin-detail deep-link in the canonical _HumanSearchResults
    // partial. Always null on the public Profile/Search page.

    public string? AdminEmail { get; set; }
    public string? MembershipStatus { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public string? AdminDetailUrl { get; set; }
}
