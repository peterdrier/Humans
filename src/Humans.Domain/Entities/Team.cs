using NodaTime;
using Humans.Domain.Attributes;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;

namespace Humans.Domain.Entities;

/// <summary>
/// Represents a working group or team within the organization.
/// </summary>
public class Team
{
    /// <summary>
    /// Unique identifier for the team.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Team name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Team description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// URL-friendly slug for the team.
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>
    /// Whether the team is currently active.
    /// </summary>
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
        ? $"{GoogleGroupPrefix}@nobodies.team"
        : null;

    /// <summary>
    /// When the team was created.
    /// </summary>
    public Instant CreatedAt { get; init; }

    /// <summary>
    /// When the team was last updated.
    /// </summary>
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

    /// <summary>
    /// Free-form markdown content for the public team page.
    /// </summary>
    [MarkdownContent]
    public string? PageContent { get; set; }

    /// <summary>
    /// When the page content was last updated.
    /// </summary>
    public Instant? PageContentUpdatedAt { get; set; }

    /// <summary>
    /// User ID of who last updated the page content.
    /// </summary>
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

    /// <summary>
    /// Optional parent team ID for one-level hierarchy (departments).
    /// A team with a parent cannot itself be a parent.
    /// </summary>
    public Guid? ParentTeamId { get; set; }

    /// <summary>
    /// Navigation property to the parent team (department).
    /// </summary>
    public Team? ParentTeam { get; set; }

    /// <summary>
    /// Navigation property to child teams (sub-teams of this department).
    /// </summary>
    public ICollection<Team> ChildTeams { get; } = new List<Team>();

    /// <summary>
    /// Navigation property to team members.
    /// </summary>
    public ICollection<TeamMember> Members { get; } = new List<TeamMember>();

    /// <summary>
    /// Navigation property to join requests.
    /// </summary>
    public ICollection<TeamJoinRequest> JoinRequests { get; } = new List<TeamJoinRequest>();

    /// <summary>
    /// Navigation property to associated Google resources.
    /// </summary>
    public ICollection<GoogleResource> GoogleResources { get; } = new List<GoogleResource>();

    /// <summary>
    /// Navigation property to legal documents scoped to this team.
    /// </summary>
    public ICollection<LegalDocument> LegalDocuments { get; } = new List<LegalDocument>();

    /// <summary>
    /// Navigation property to role definitions.
    /// </summary>
    public ICollection<TeamRoleDefinition> RoleDefinitions { get; } = new List<TeamRoleDefinition>();

    /// <summary>
    /// Whether this is a system-managed team.
    /// </summary>
    public bool IsSystemTeam => SystemTeamType != SystemTeamType.None;

    /// <summary>
    /// Display name including parent prefix for sub-teams (e.g. "Comms - Logo").
    /// Requires ParentTeam navigation to be loaded.
    /// </summary>
    public string DisplayName => ParentTeam is not null ? $"{ParentTeam.Name} - {Name}" : Name;
}
