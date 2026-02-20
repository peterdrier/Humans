using System.ComponentModel.DataAnnotations;
using Humans.Domain.Enums;

namespace Humans.Web.Models;

public class TeamIndexViewModel
{
    public List<TeamSummaryViewModel> MyTeams { get; set; } = [];
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
    public bool IsCurrentUserLead { get; set; }
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
    public List<TeamResourceLinkViewModel> Resources { get; set; } = [];

    // Current user context
    public bool IsCurrentUserMember { get; set; }
    public bool IsCurrentUserLead { get; set; }
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
    public bool HasCustomProfilePicture { get; set; }
    public string? CustomProfilePictureUrl { get; set; }
    public string Role { get; set; } = string.Empty;
    public DateTime JoinedAt { get; set; }
    public bool IsLead { get; set; }

    /// <summary>
    /// The effective profile picture URL (custom upload takes priority over Google avatar).
    /// </summary>
    public string? EffectiveProfilePictureUrl => HasCustomProfilePicture
        ? CustomProfilePictureUrl
        : ProfilePictureUrl;
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
    public bool IsLead { get; set; }
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
    public string TeamSlug { get; set; } = string.Empty;
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

public class BirthdayCalendarViewModel
{
    public List<BirthdayEntryViewModel> Birthdays { get; set; } = [];
    public int CurrentMonth { get; set; }
    public string CurrentMonthName { get; set; } = string.Empty;
}

public class BirthdayEntryViewModel
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? EffectiveProfilePictureUrl { get; set; }
    public int DayOfMonth { get; set; }
    public int Month { get; set; }
    public string MonthName { get; set; } = string.Empty;
    public List<string> TeamNames { get; set; } = [];
}

public class MapViewModel
{
    public List<MapMarkerViewModel> Markers { get; set; } = [];
}

public class MapMarkerViewModel
{
    public string DisplayName { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? City { get; set; }
    public string? CountryCode { get; set; }
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

/// <summary>
/// Simplified resource link for display on team detail page.
/// </summary>
public class TeamResourceLinkViewModel
{
    public string Name { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string IconClass { get; set; } = string.Empty;
}
