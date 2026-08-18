using Humans.Base.Authorization;

namespace Humans.Users.Models;

/// <summary>
/// Compact profile summary for inline display ("baseball card").
/// </summary>
internal sealed class ProfileSummaryViewModel
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? ProfilePictureUrl { get; set; }
    public string? PreferredLanguage { get; set; }
    public string? MembershipTier { get; set; }
    public string? MembershipStatus { get; set; }
    public DateTime? MemberSince { get; set; }
    public DateTime? LastLogin { get; set; }
    public string? City { get; set; }
    public string? CountryCode { get; set; }
    public bool IsSuspended { get; set; }
    public List<string> Teams { get; set; } = [];

    /// <summary>
    /// Teams the subject belongs to that are flagged <c>IsHidden</c>. Only populated
    /// in the popover render path when the viewer is TeamsAdmin/Board/Admin — kept
    /// separate from <see cref="Teams"/> so the popover can render an admin-only
    /// section below a separator.
    /// </summary>
    public List<string> HiddenTeams { get; set; } = [];

    public IReadOnlyList<ProfileLanguageDisplayViewModel> Languages { get; set; } = [];

    /// <summary>
    /// The subject's camp for the active season, if they're an active member of one.
    /// Only rendered in the popover when the viewer holds an admin-shaped role
    /// (<see cref="PolicyNames.AnyAdminRole"/>). Null when not in a camp this year.
    /// </summary>
    public string? CampName { get; set; }

    /// <summary>
    /// Named camp roles the subject holds in <see cref="CampName"/> (e.g. "Camp Lead",
    /// "Greeter"), ordered by role sort order. Empty when none. Same admin-only gate as
    /// <see cref="CampName"/>.
    /// </summary>
    public IReadOnlyList<string> CampRoles { get; set; } = [];

    /// <summary>
    /// False when the user exists (AspNetUsers row) but has no Profile row —
    /// e.g. mailing-list / ticketing imports. The popover renders a sparse
    /// "imported account" card in that case instead of 404'ing.
    /// </summary>
    public bool HasProfile { get; set; } = true;
}
