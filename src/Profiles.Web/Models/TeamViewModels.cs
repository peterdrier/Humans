using System.ComponentModel.DataAnnotations;
using Profiles.Domain.Enums;

namespace Profiles.Web.Models;

public class TeamIndexViewModel
{
    public List<TeamSummaryViewModel> Teams { get; set; } = [];
    public bool CanCreateTeam { get; set; }
    public int TotalCount { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 12;
}

public class TeamSummaryViewModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Slug { get; set; } = string.Empty;
    public int MemberCount { get; set; }
    public bool IsSystemTeam { get; set; }
    public bool RequiresApproval { get; set; }
    public bool IsCurrentUserMember { get; set; }
    public bool IsCurrentUserMetalead { get; set; }
}

public class TeamDetailViewModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Slug { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool RequiresApproval { get; set; }
    public bool IsSystemTeam { get; set; }
    public string? SystemTeamType { get; set; }
    public DateTime CreatedAt { get; set; }

    public List<TeamMemberViewModel> Members { get; set; } = [];

    // Current user context
    public bool IsCurrentUserMember { get; set; }
    public bool IsCurrentUserMetalead { get; set; }
    public bool CanCurrentUserJoin { get; set; }
    public bool CanCurrentUserLeave { get; set; }
    public bool CanCurrentUserManage { get; set; }
    public Guid? CurrentUserPendingRequestId { get; set; }
    public int PendingRequestCount { get; set; }
}

public class TeamMemberViewModel
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? ProfilePictureUrl { get; set; }
    public string Role { get; set; } = string.Empty;
    public DateTime JoinedAt { get; set; }
    public bool IsMetalead { get; set; }
}

public class MyTeamsViewModel
{
    public List<MyTeamMembershipViewModel> Memberships { get; set; } = [];
    public List<TeamJoinRequestSummaryViewModel> PendingRequests { get; set; } = [];
}

public class MyTeamMembershipViewModel
{
    public Guid TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public string TeamSlug { get; set; } = string.Empty;
    public bool IsSystemTeam { get; set; }
    public string Role { get; set; } = string.Empty;
    public bool IsMetalead { get; set; }
    public DateTime JoinedAt { get; set; }
    public bool CanLeave { get; set; }
    public int PendingRequestCount { get; set; }
}

public class TeamJoinRequestSummaryViewModel
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public string TeamSlug { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string StatusBadgeClass { get; set; } = "bg-secondary";
    public DateTime RequestedAt { get; set; }
}

public class JoinTeamViewModel
{
    public Guid TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public bool RequiresApproval { get; set; }

    [StringLength(2000)]
    public string? Message { get; set; }
}

public class TeamJoinRequestViewModel
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public string UserDisplayName { get; set; } = string.Empty;
    public string UserEmail { get; set; } = string.Empty;
    public string? UserProfilePictureUrl { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Message { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ReviewedByName { get; set; }
    public string? ReviewNotes { get; set; }
}

public class PendingRequestsViewModel
{
    public List<TeamJoinRequestViewModel> Requests { get; set; } = [];
    public Guid? TeamIdFilter { get; set; }
    public string? TeamNameFilter { get; set; }
    public int TotalCount { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public class CreateTeamViewModel
{
    [Required]
    [StringLength(256, MinimumLength = 2)]
    public string Name { get; set; } = string.Empty;

    [StringLength(2000)]
    public string? Description { get; set; }

    public bool RequiresApproval { get; set; } = true;
}

public class EditTeamViewModel
{
    public Guid Id { get; set; }

    [Required]
    [StringLength(256, MinimumLength = 2)]
    public string Name { get; set; } = string.Empty;

    [StringLength(2000)]
    public string? Description { get; set; }

    public bool RequiresApproval { get; set; }
    public bool IsActive { get; set; }
    public bool IsSystemTeam { get; set; }
}

public class TeamMembersViewModel
{
    public Guid TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public string TeamSlug { get; set; } = string.Empty;
    public bool IsSystemTeam { get; set; }
    public List<TeamMemberViewModel> Members { get; set; } = [];
    public bool CanManageRoles { get; set; }
    public int TotalCount { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public class SetMemberRoleModel
{
    public Guid TeamId { get; set; }
    public Guid UserId { get; set; }
    public TeamMemberRole Role { get; set; }
}

public class ApproveRejectRequestModel
{
    public Guid RequestId { get; set; }
    public string? Notes { get; set; }
}

public class AdminTeamListViewModel
{
    public List<AdminTeamViewModel> Teams { get; set; } = [];
    public int TotalCount { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public class AdminTeamViewModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool RequiresApproval { get; set; }
    public bool IsSystemTeam { get; set; }
    public string? SystemTeamType { get; set; }
    public int MemberCount { get; set; }
    public int PendingRequestCount { get; set; }
    public DateTime CreatedAt { get; set; }
}
