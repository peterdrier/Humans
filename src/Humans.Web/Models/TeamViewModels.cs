using System.ComponentModel.DataAnnotations;
using Humans.Domain.Entities;
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
    public List<TeamRoleDefinitionViewModel> RoleDefinitions { get; set; } = [];

    // Current user context
    public bool IsCurrentUserMember { get; set; }
    public bool IsCurrentUserLead { get; set; }
    public bool CanCurrentUserJoin { get; set; }
    public bool CanCurrentUserLeave { get; set; }
    public bool CanCurrentUserManage { get; set; }
    public bool CanCurrentUserEditTeam { get; set; }
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

    [StringLength(64)]
    [RegularExpression(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?$", ErrorMessage = "Only lowercase letters, numbers, and hyphens allowed")]
    public string? GoogleGroupPrefix { get; set; }

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

    [StringLength(64)]
    [RegularExpression(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?$", ErrorMessage = "Only lowercase letters, numbers, and hyphens allowed")]
    public string? GoogleGroupPrefix { get; set; }

    // Display-only — computed from prefix
    public string? GoogleGroupEmail { get; set; }

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
    public List<TeamJoinRequestViewModel> PendingRequests { get; set; } = [];
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
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? ProfilePictureUrl { get; set; }
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

public class AddMemberModel
{
    public Guid UserId { get; set; }
}

public class TeamRoleDefinitionViewModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SlotCount { get; set; }
    public List<TeamRoleSlotViewModel> Slots { get; set; } = [];
    public int SortOrder { get; set; }
    public bool IsLeadRole { get; set; }

    /// <summary>
    /// IDs of members already assigned to this role (for filtering dropdowns).
    /// </summary>
    public HashSet<Guid> AssignedUserIds { get; set; } = [];

    public static TeamRoleDefinitionViewModel FromEntity(TeamRoleDefinition d)
    {
        var slots = new List<TeamRoleSlotViewModel>();
        var assignedUserIds = new HashSet<Guid>();

        for (var i = 0; i < d.SlotCount; i++)
        {
            var assignment = d.Assignments.FirstOrDefault(a => a.SlotIndex == i);
            var priority = i < d.Priorities.Count ? d.Priorities[i] : SlotPriority.None;
            slots.Add(new TeamRoleSlotViewModel
            {
                SlotIndex = i,
                Priority = priority.ToString(),
                PriorityBadgeClass = priority switch
                {
                    SlotPriority.Critical => "bg-danger",
                    SlotPriority.Important => "bg-warning text-dark",
                    SlotPriority.NiceToHave => "bg-secondary",
                    _ => "bg-light text-dark"
                },
                IsFilled = assignment != null,
                AssignedUserId = assignment?.TeamMember?.UserId,
                AssignedUserName = assignment?.TeamMember?.User?.DisplayName,
                TeamMemberId = assignment?.TeamMemberId
            });

            if (assignment?.TeamMember?.UserId != null)
                assignedUserIds.Add(assignment.TeamMember.UserId);
        }

        return new TeamRoleDefinitionViewModel
        {
            Id = d.Id,
            Name = d.Name,
            Description = d.Description,
            SlotCount = d.SlotCount,
            Slots = slots,
            SortOrder = d.SortOrder,
            IsLeadRole = d.IsLeadRole,
            AssignedUserIds = assignedUserIds
        };
    }
}

public class TeamRoleSlotViewModel
{
    public int SlotIndex { get; set; }
    public string Priority { get; set; } = string.Empty;
    public string PriorityBadgeClass { get; set; } = string.Empty;
    public bool IsFilled { get; set; }
    public Guid? AssignedUserId { get; set; }
    public string? AssignedUserName { get; set; }
    public string? AssignedUserProfilePictureUrl { get; set; }
    public Guid? TeamMemberId { get; set; }
}

public class RoleManagementViewModel
{
    public Guid TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public bool IsSystemTeam { get; set; }
    public bool CanManage { get; set; }
    public List<TeamRoleDefinitionViewModel> RoleDefinitions { get; set; } = [];
    public List<TeamMemberViewModel> TeamMembers { get; set; } = [];
}

public class CreateRoleDefinitionModel
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SlotCount { get; set; } = 1;
    public List<string> Priorities { get; set; } = ["None"];
    public int SortOrder { get; set; }
}

public class EditRoleDefinitionModel
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SlotCount { get; set; }
    public List<string> Priorities { get; set; } = [];
    public int SortOrder { get; set; }
}

public class AssignRoleModel
{
    public Guid UserId { get; set; }
}

public class RosterSummaryViewModel
{
    public List<RosterSlotViewModel> Slots { get; set; } = [];
    public string? PriorityFilter { get; set; }
    public string? StatusFilter { get; set; }
}

public class RosterSlotViewModel
{
    public string TeamName { get; set; } = string.Empty;
    public string TeamSlug { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
    public int SlotNumber { get; set; }
    public string Priority { get; set; } = string.Empty;
    public string PriorityBadgeClass { get; set; } = string.Empty;
    public bool IsFilled { get; set; }
    public string? AssignedUserName { get; set; }
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
    public bool HasMailGroup { get; set; }
    public int DriveResourceCount { get; set; }
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
