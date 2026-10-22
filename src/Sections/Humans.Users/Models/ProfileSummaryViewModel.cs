using Humans.Base.Authorization;

namespace Humans.Users.Models;

/// <summary>Compact profile summary for inline display ("baseball card").</summary>
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
    /// Hidden teams, popover only and only for TeamsAdmin/Board/Admin viewers; kept apart from
    /// <see cref="Teams"/> so they render below a separator.
    /// </summary>
    public List<string> HiddenTeams { get; set; } = [];

    public IReadOnlyList<ProfileLanguageDisplayViewModel> Languages { get; set; } = [];

    /// <summary>
    /// Active-season camp, rendered only for <see cref="PolicyNames.AnyAdminRole"/> viewers.
    /// </summary>
    public string? CampName { get; set; }

    /// <summary>Ordered by role sort order; same admin-only gate as <see cref="CampName"/>.</summary>
    public IReadOnlyList<string> CampRoles { get; set; } = [];

    /// <summary>
    /// False for inline summaries whose referenced user or Profile row is unavailable.
    /// The hover-popover endpoint requires a Profile and returns 404 without one.
    /// </summary>
    public bool HasProfile { get; set; } = true;
}
