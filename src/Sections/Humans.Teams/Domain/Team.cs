using NodaTime;
using Humans.Base.Attributes;
using Humans.Base.Constants;
using Humans.Base.Enums;

using Humans.Teams.Contracts;
namespace Humans.Teams.Domain;

internal sealed class Team
{
    public Guid Id { get; init; }

    public string Name { get; set; } = string.Empty;

    [MarkdownContent]
    public string? Description { get; set; }

    public string Slug { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    /// <summary>Approval by a coordinator or board member.</summary>
    public bool RequiresApproval { get; set; } = true;

    public SystemTeamType SystemTeamType { get; set; } = SystemTeamType.None;

    /// <summary>The part before @nobodies.team. Null means no group.</summary>
    public string? GoogleGroupPrefix { get; set; }

    public string? GoogleGroupEmail => GoogleGroupPrefix is not null
        ? $"{GoogleGroupPrefix}@{DomainConstants.GoogleGroupDomain}"
        : null;

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }

    /// <summary>Overrides the auto-generated slug for external URL stability; both resolve to this team.</summary>
    public string? CustomSlug { get; set; }

    /// <summary>Only departments (no parent, non-system) can be public.</summary>
    public bool IsPublicPage { get; set; }

    public bool ShowCoordinatorsOnPublicPage { get; set; } = true;

    [MarkdownContent]
    public string? PageContent { get; set; }

    public Instant? PageContentUpdatedAt { get; set; }

    public Guid? PageContentUpdatedByUserId { get; set; }

    /// <summary>At most 3. Stored as JSONB.</summary>
    public List<CallToAction>? CallsToAction { get; set; }

    /// <summary>When true, a BudgetCategory is auto-created under the Departments group on budget year creation.</summary>
    public bool HasBudget { get; set; }

    /// <summary>Hidden from non-admins. Campaigns can still target hidden teams for code distribution.</summary>
    public bool IsHidden { get; set; }

    /// <summary>
    /// Admin-only flag. Adding or approving members shows a deterrent modal with the audit record that will be created.
    /// </summary>
    public bool IsSensitive { get; set; }

    /// <summary>Toggling it off never deletes existing grants; they stop appearing while disabled.</summary>
    public bool EarlyEntryEnabled { get; set; }

    /// <summary>One level only: a team with a parent can't itself be a parent.</summary>
    public Guid? ParentTeamId { get; set; }

    public Team? ParentTeam { get; set; }

    public ICollection<Team> ChildTeams { get; } = new List<Team>();

    public ICollection<TeamMember> Members { get; } = new List<TeamMember>();

    public ICollection<TeamEarlyEntryGrant> EarlyEntryGrants { get; } = new List<TeamEarlyEntryGrant>();

    public ICollection<TeamJoinRequest> JoinRequests { get; } = new List<TeamJoinRequest>();

    public ICollection<TeamRoleDefinition> RoleDefinitions { get; } = new List<TeamRoleDefinition>();

    /// <summary>Only meaningful for subteams; top-level teams always appear.</summary>
    public bool IsPromotedToDirectory { get; set; }

    /// <summary>Not mapped: EF queries need the inline expression.</summary>
    public bool IsInDirectory => ParentTeamId == null || IsPromotedToDirectory;

    public bool IsSystemTeam => SystemTeamType != SystemTeamType.None;

    /// <summary>Requires ParentTeam to be loaded.</summary>
    public string DisplayName => ParentTeam is not null ? $"{ParentTeam.Name} - {Name}" : Name;
}
