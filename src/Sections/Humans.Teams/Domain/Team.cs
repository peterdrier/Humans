using NodaTime;
using Humans.Base.Attributes;
using Humans.Base.Constants;
using Humans.Base.Enums;

using Humans.Teams.Contracts;
namespace Humans.Teams.Domain;

/// <summary>
/// Represents a working group or team within the organization.
/// </summary>
internal sealed class Team
{
    public Guid Id { get; init; }

    public string Name { get; set; } = string.Empty;

    [MarkdownContent]
    public string? Description { get; set; }

    public string Slug { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Whether joining this team requires approval from a coordinator or board member.
    /// </summary>
    public bool RequiresApproval { get; set; } = true;

    /// <summary>
    /// Identifies system-managed teams with automatic membership sync.
    /// </summary>
    public SystemTeamType SystemTeamType { get; set; } = SystemTeamType.None;

    /// <summary>
    /// Google Group email prefix (before @nobodies.team). Null means no group for this team.
    /// </summary>
    public string? GoogleGroupPrefix { get; set; }

    /// <summary>
    /// Full Google Group email address, or null if no prefix is set.
    /// </summary>
    public string? GoogleGroupEmail => GoogleGroupPrefix is not null
        ? $"{GoogleGroupPrefix}@{DomainConstants.GoogleGroupDomain}"
        : null;

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }

    /// <summary>
    /// Optional custom slug that overrides the auto-generated slug for external URL stability.
    /// When set, both the custom slug and the auto-generated slug resolve to this team.
    /// </summary>
    public string? CustomSlug { get; set; }

    /// <summary>
    /// Whether this team has a public-facing page visible to anonymous visitors.
    /// Only departments (no parent, non-system) can be made public.
    /// </summary>
    public bool IsPublicPage { get; set; }

    /// <summary>
    /// Whether coordinators are shown on the public page. Default true.
    /// </summary>
    public bool ShowCoordinatorsOnPublicPage { get; set; } = true;

    [MarkdownContent]
    public string? PageContent { get; set; }

    public Instant? PageContentUpdatedAt { get; set; }

    public Guid? PageContentUpdatedByUserId { get; set; }

    /// <summary>
    /// Call-to-action buttons displayed on the public team page (max 3).
    /// Stored as JSONB.
    /// </summary>
    public List<CallToAction>? CallsToAction { get; set; }

    /// <summary>
    /// Whether this team participates in budget planning.
    /// When true, a BudgetCategory is auto-created under the Departments group on budget year creation.
    /// </summary>
    public bool HasBudget { get; set; }

    /// <summary>
    /// Whether this team is hidden from non-admin users.
    /// Hidden teams do not appear on profile cards, team listings, or public pages,
    /// but remain fully visible and manageable by Admin/TeamsAdmin.
    /// Campaigns can still target hidden teams for code distribution.
    /// </summary>
    public bool IsHidden { get; set; }

    /// <summary>
    /// Whether this team handles sensitive information. Admin-only flag, not publicly visible.
    /// When true, adding or approving members triggers a deterrent confirmation modal
    /// showing the audit record that will be created.
    /// </summary>
    public bool IsSensitive { get; set; }

    /// <summary>Per-team early-entry gate: when true this team contributes early-entry
    /// grants (see <see cref="TeamEarlyEntryGrant"/>) and exposes the per-team EE
    /// management page to the team's coordinators and the cross-team EETeamAdmin role.
    /// Multiple teams may have this enabled. Default false. Toggling it never deletes
    /// existing grants.</summary>
    public bool EarlyEntryEnabled { get; set; }

    /// <summary>
    /// Optional parent team ID for one-level hierarchy (departments).
    /// A team with a parent cannot itself be a parent.
    /// </summary>
    public Guid? ParentTeamId { get; set; }

    public Team? ParentTeam { get; set; }

    public ICollection<Team> ChildTeams { get; } = new List<Team>();

    public ICollection<TeamMember> Members { get; } = new List<TeamMember>();

    public ICollection<TeamEarlyEntryGrant> EarlyEntryGrants { get; } = new List<TeamEarlyEntryGrant>();

    public ICollection<TeamJoinRequest> JoinRequests { get; } = new List<TeamJoinRequest>();

    public ICollection<TeamRoleDefinition> RoleDefinitions { get; } = new List<TeamRoleDefinition>();

    /// <summary>
    /// Whether this subteam is promoted to appear on the Teams directory page.
    /// Only meaningful for subteams (ParentTeamId != null). Top-level teams always appear.
    /// </summary>
    public bool IsPromotedToDirectory { get; set; }

    /// <summary>
    /// Whether this team should appear in the Teams directory.
    /// Top-level teams always appear; subteams only if promoted.
    /// Not mapped to DB — use inline expression for EF queries.
    /// </summary>
    public bool IsInDirectory => ParentTeamId == null || IsPromotedToDirectory;

    public bool IsSystemTeam => SystemTeamType != SystemTeamType.None;

    /// <summary>
    /// Display name including parent prefix for sub-teams (e.g. "Comms - Logo").
    /// Requires ParentTeam navigation to be loaded.
    /// </summary>
    public string DisplayName => ParentTeam is not null ? $"{ParentTeam.Name} - {Name}" : Name;
}
