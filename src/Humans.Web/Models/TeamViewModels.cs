using System.ComponentModel.DataAnnotations;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;

namespace Humans.Web.Models;

public class TeamIndexViewModel
{
    public List<TeamSummaryViewModel> MyTeams { get; set; } = [];
    public List<TeamSummaryViewModel> Departments { get; set; } = [];
    public List<TeamSummaryViewModel> SystemTeams { get; set; } = [];
    public bool CanCreateTeam { get; set; }
    public bool IsAuthenticated { get; set; }
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
    public bool IsPublicPage { get; set; }
    public bool IsCurrentUserMember { get; set; }
    public bool IsCurrentUserCoordinator { get; set; }
    public string? ParentTeamName { get; set; }
    public string? ParentTeamSlug { get; set; }

    /// <summary>
    /// Sort key that groups sub-teams under their parent: "ParentName - ChildName" or just "Name".
    /// </summary>
    public string SortKey => ParentTeamName is not null ? $"{ParentTeamName} - {Name}" : Name;
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
    public SystemTeamType? SystemTeamType { get; set; }
    public DateTime CreatedAt { get; set; }

    public Team? ParentTeam { get; init; }
    public IReadOnlyList<Team> ChildTeams { get; init; } = [];

    public List<TeamMemberViewModel> Members { get; set; } = [];
    public List<TeamResourceLinkViewModel> Resources { get; set; } = [];
    public List<TeamRoleDefinitionViewModel> RoleDefinitions { get; set; } = [];

    // Public page content
    public bool IsPublicPage { get; set; }
    public bool ShowCoordinatorsOnPublicPage { get; set; }
    public string? PageContent { get; set; }
    public string? PageContentHtml { get; set; }
    public List<CallToAction>? CallsToAction { get; set; }
    public DateTime? PageContentUpdatedAt { get; set; }
    public string? PageContentUpdatedByDisplayName { get; set; }

    // Viewer context
    public bool IsAuthenticated { get; set; }
    public bool CanEditPageContent { get; set; }

    // Current user context
    public bool IsCurrentUserMember { get; set; }
    public bool IsCurrentUserCoordinator { get; set; }
    public bool CanCurrentUserJoin { get; set; }
    public bool CanCurrentUserLeave { get; set; }
    public bool CanCurrentUserManage { get; set; }
    public bool CanCurrentUserEditTeam { get; set; }
    public Guid? CurrentUserPendingRequestId { get; set; }
    public int PendingRequestCount { get; set; }
    public ShiftsSummaryCardViewModel? ShiftsSummary { get; set; }
}

public class TeamMemberViewModel
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? ProfilePictureUrl { get; set; }
    public bool HasCustomProfilePicture { get; set; }
    public string? CustomProfilePictureUrl { get; set; }
    public TeamMemberRole Role { get; set; }
    public DateTime JoinedAt { get; set; }
    public bool IsCoordinator { get; set; }

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
    public TeamMemberRole Role { get; set; }
    public bool IsCoordinator { get; set; }
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
    public TeamJoinRequestStatus Status { get; set; }
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
    public TeamJoinRequestStatus Status { get; set; }
    public string? Message { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ReviewedByName { get; set; }
    public string? ReviewNotes { get; set; }
}

public class PendingRequestsViewModel : PagedListViewModel
{
    public PendingRequestsViewModel() : base(20)
    {
    }

    public List<TeamJoinRequestViewModel> Requests { get; set; } = [];
    public Guid? TeamIdFilter { get; set; }
    public string? TeamNameFilter { get; set; }
}

public class CreateTeamViewModel : TeamFormViewModelBase
{
}

public class EditTeamViewModel : TeamFormViewModelBase
{
    public Guid Id { get; set; }

    // Display-only
    public string? GoogleGroupEmail { get; set; }
    public string Slug { get; set; } = string.Empty;

    /// <summary>
    /// Optional custom slug that overrides the auto-generated slug for external URL stability.
    /// </summary>
    [StringLength(256)]
    [RegularExpression(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?$", ErrorMessage = "Only lowercase letters, numbers, and hyphens allowed")]
    public string? CustomSlug { get; set; }

    public bool IsActive { get; set; }
    public bool IsSystemTeam { get; set; }
    public bool HasBudget { get; set; }
}

public class EditTeamPageViewModel
{
    public Guid TeamId { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string TeamName { get; set; } = string.Empty;
    public bool IsPublicPage { get; set; }
    public bool ShowCoordinatorsOnPublicPage { get; set; } = true;
    public bool CanBePublic { get; set; }

    [StringLength(50000)]
    public string? PageContent { get; set; }

    public List<CallToActionViewModel> CallsToAction { get; set; } = [];
}

public class CallToActionViewModel
{
    [StringLength(100)]
    public string? Text { get; set; }

    [StringLength(512)]
    public string? Url { get; set; }

    public CallToActionStyle Style { get; set; } = CallToActionStyle.Secondary;
}

public class TeamMembersViewModel
    : PagedListViewModel
{
    public TeamMembersViewModel() : base(20)
    {
    }

    public Guid TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public string TeamSlug { get; set; } = string.Empty;
    public bool IsSystemTeam { get; set; }
    public List<TeamMemberViewModel> Members { get; set; } = [];
    public List<TeamJoinRequestViewModel> PendingRequests { get; set; } = [];
    public bool CanManageRoles { get; set; }
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
    public bool IsManagement { get; set; }
    public RolePeriod Period { get; set; }

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
                Priority = priority,
                PriorityBadgeClass = priority switch
                {
                    SlotPriority.Critical => "bg-danger",
                    SlotPriority.Important => "bg-warning text-dark",
                    SlotPriority.NiceToHave => "bg-secondary",
                    _ => "bg-light text-dark"
                },
                IsFilled = assignment is not null,
                AssignedUserId = assignment?.TeamMember?.UserId,
                AssignedUserName = assignment?.TeamMember?.User?.DisplayName,
                TeamMemberId = assignment?.TeamMemberId
            });

            if (assignment?.TeamMember?.UserId is not null)
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
            IsManagement = d.IsManagement,
            Period = d.Period,
            AssignedUserIds = assignedUserIds
        };
    }
}

public class TeamRoleSlotViewModel
{
    public int SlotIndex { get; set; }
    public SlotPriority Priority { get; set; }
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
    public bool IsChildTeam { get; set; }
    public bool CanManage { get; set; }
    public List<TeamRoleDefinitionViewModel> RoleDefinitions { get; set; } = [];
    public List<TeamMemberViewModel> TeamMembers { get; set; } = [];
}

public class CreateRoleDefinitionModel
{
    public string Name { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }
    public int SlotCount { get; set; } = 1;
    public List<string> Priorities { get; set; } = ["None"];
    public int SortOrder { get; set; }
    public RolePeriod Period { get; set; } = RolePeriod.YearRound;
}

public class EditRoleDefinitionModel
{
    public string Name { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }
    public int SlotCount { get; set; }
    public List<string> Priorities { get; set; } = [];
    public int SortOrder { get; set; }
    public bool IsManagement { get; set; }
    public RolePeriod Period { get; set; } = RolePeriod.YearRound;
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
    public string? PeriodFilter { get; set; }
}

public class RosterSlotViewModel
{
    public string TeamName { get; set; } = string.Empty;
    public string TeamSlug { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
    public string? RoleDescription { get; set; }
    public Guid RoleDefinitionId { get; set; }
    public int SlotNumber { get; set; }
    public SlotPriority Priority { get; set; }
    public string PriorityBadgeClass { get; set; } = string.Empty;
    public RolePeriod Period { get; set; }
    public bool IsFilled { get; set; }
    public Guid? AssignedUserId { get; set; }
    public string? AssignedUserName { get; set; }
}

public class HumanSearchViewModel
{
    public string? Query { get; set; }
    public List<HumanSearchResultViewModel> Results { get; set; } = [];
}

public class HumanSearchResultViewModel
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? BurnerName { get; set; }
    public string? City { get; set; }
    public string? Bio { get; set; }
    public string? ContributionInterests { get; set; }
    public string? EffectiveProfilePictureUrl { get; set; }
    public string? MatchField { get; set; }
    public string? MatchSnippet { get; set; }
}

public class AdminTeamListViewModel : PagedListViewModel
{
    public AdminTeamListViewModel() : base(20)
    {
    }

    public List<AdminTeamViewModel> Teams { get; set; } = [];
}

public class AdminTeamViewModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool RequiresApproval { get; set; }
    public bool IsSystemTeam { get; set; }
    public SystemTeamType? SystemTeamType { get; set; }
    public int MemberCount { get; set; }
    public int PendingRequestCount { get; set; }
    public bool HasMailGroup { get; set; }
    public string? GoogleGroupEmail { get; set; }
    public int DriveResourceCount { get; set; }
    public int RoleSlotCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsChildTeam { get; set; }
    public int PendingShiftSignupCount { get; set; }
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
