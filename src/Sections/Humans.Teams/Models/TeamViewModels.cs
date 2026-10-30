using Microsoft.AspNetCore.Mvc.ModelBinding;
using System.ComponentModel.DataAnnotations;
using Humans.Teams.Contracts;
using Humans.Teams.Services;
using Humans.Base.Enums;
using NodaTime;

using Humans.Base.Models;

namespace Humans.Teams.Models;

internal sealed class TeamIndexViewModel
{
    public List<TeamSummaryViewModel> MyTeams { get; set; } = [];
    public List<TeamSummaryViewModel> Departments { get; set; } = [];
    public List<TeamSummaryViewModel> SystemTeams { get; set; } = [];
    public List<TeamSummaryViewModel> HiddenTeams { get; set; } = [];
    public bool CanCreateTeam { get; set; }
    public bool IsAuthenticated { get; set; }
}

internal sealed class TeamTreeViewModel
{
    public IReadOnlyList<TeamTreeNode> Departments { get; init; } = [];
}

internal sealed class TeamSummaryViewModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Slug { get; set; } = string.Empty;
    public int MemberCount { get; set; }
    public bool IsSystemTeam { get; set; }
    public bool IsHidden { get; set; }
    public bool RequiresApproval { get; set; }
    public bool IsPublicPage { get; set; }
    public bool IsCurrentUserMember { get; set; }
    public bool IsCurrentUserCoordinator { get; set; }
    public string? ParentTeamName { get; set; }
    public string? ParentTeamSlug { get; set; }

    /// <summary>Groups sub-teams under their parent.</summary>
    public string SortKey => ParentTeamName is not null ? $"{ParentTeamName} - {Name}" : Name;
}

internal sealed class TeamDetailViewModel
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

    public TeamPageTeamLink? ParentTeam { get; init; }
    public IReadOnlyList<TeamPageTeamLink> ChildTeams { get; init; } = [];

    public List<TeamMemberViewModel> Members { get; set; } = [];
    public List<TeamResourceLinkViewModel> Resources { get; set; } = [];
    public List<TeamRoleDefinitionViewModel> RoleDefinitions { get; set; } = [];

    public bool IsPublicPage { get; set; }
    public bool ShowCoordinatorsOnPublicPage { get; set; }
    public string? PageContent { get; set; }
    public string? PageContentHtml { get; set; }
    public List<CallToAction>? CallsToAction { get; set; }
    public DateTime? PageContentUpdatedAt { get; set; }
    public string? PageContentUpdatedByDisplayName { get; set; }

    public bool IsAuthenticated { get; set; }
    public bool CanEditPageContent { get; set; }

    public bool IsCurrentUserMember { get; set; }
    public bool IsCurrentUserCoordinator { get; set; }
    public bool CanCurrentUserJoin { get; set; }
    public bool CanCurrentUserLeave { get; set; }
    public bool CanCurrentUserManage { get; set; }
    public bool CanCurrentUserEditTeam { get; set; }

    /// <summary>
    /// Early entry is enabled and the viewer can manage it: a coordinator, EETeamAdmin, or TeamsAdmin/Board/Admin.
    /// </summary>
    public bool CanManageEarlyEntry { get; set; }
    public Guid? CurrentUserPendingRequestId { get; set; }
    public int PendingRequestCount { get; set; }
    public ShiftsSummaryCardViewModel? ShiftsSummary { get; set; }

    /// <summary>Departments only.</summary>
    public List<ChildTeamMemberViewModel> SubteamLeads { get; set; } = [];

    /// <summary>Departments only.</summary>
    public List<ChildTeamMemberViewModel> ChildTeamMembers { get; set; } = [];
}

internal sealed class ChildTeamMemberViewModel
{
    public Guid UserId { get; set; }
    public string ChildTeamName { get; set; } = string.Empty;
    public string ChildTeamSlug { get; set; } = string.Empty;
    public bool IsCoordinator { get; set; }
    public string? RoleTitle { get; set; }
}

internal sealed class TeamMemberViewModel
{
    public Guid UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public TeamMemberRole Role { get; set; }
    public DateTime JoinedAt { get; set; }
    public bool IsCoordinator { get; set; }

    public string? NobodiesTeamEmail { get; set; }

    /// <summary>The merge survivor's id: the provisioning form must post this, not a merged-away id.</summary>
    public Guid LiveUserId { get; set; }
}

internal sealed class MyTeamsViewModel
{
    public List<MyTeamMembershipViewModel> Memberships { get; set; } = [];
    public List<TeamJoinRequestSummaryViewModel> PendingRequests { get; set; } = [];
}

internal sealed class MyTeamMembershipViewModel
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

internal sealed class TeamJoinRequestSummaryViewModel
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public string TeamSlug { get; set; } = string.Empty;
    public TeamJoinRequestStatus Status { get; set; }
    public string StatusBadgeClass { get; set; } = "bg-secondary";
    public DateTime RequestedAt { get; set; }
}

internal sealed class JoinTeamViewModel
{
    public Guid TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public string TeamSlug { get; set; } = string.Empty;
    public bool RequiresApproval { get; set; }

    [StringLength(2000, ErrorMessage = "Validation_MaxLength")]
    public string? Message { get; set; }
}

internal sealed class TeamJoinRequestViewModel
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public TeamJoinRequestStatus Status { get; set; }
    public string? Message { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ReviewedByName { get; set; }
    public string? ReviewNotes { get; set; }
}

internal sealed class CreateTeamViewModel : TeamFormViewModelBase;

internal sealed class EditTeamViewModel : TeamFormViewModelBase
{
    public Guid Id { get; set; }

    public string? GoogleGroupEmail { get; set; }
    public string Slug { get; set; } = string.Empty;

    /// <summary>Overrides the auto-generated slug for external URL stability.</summary>
    [StringLength(256)]
    [RegularExpression(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?$", ErrorMessage = "Only lowercase letters, numbers, and hyphens allowed")]
    public string? CustomSlug { get; set; }

    public bool IsActive { get; set; }
    public bool IsSystemTeam { get; set; }
    public bool HasBudget { get; set; }
    public bool IsSensitive { get; set; }
    public bool IsPromotedToDirectory { get; set; }

    [Display(Name = "Enable Early Entry")]
    public bool EarlyEntryEnabled { get; set; }
}

internal sealed class EditTeamPageViewModel
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

internal sealed class CallToActionViewModel
{
    [StringLength(100)]
    public string? Text { get; set; }

    [StringLength(512)]
    public string? Url { get; set; }

    public CallToActionStyle Style { get; set; } = CallToActionStyle.Secondary;
}

internal sealed class TeamMembersViewModel
    : PagedListViewModel
{
    public Guid TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public string TeamSlug { get; set; } = string.Empty;
    public bool IsSystemTeam { get; set; }

    /// <summary>False for Board, which reads the page and changes nothing.</summary>
    public bool CanManage { get; set; }

    public List<TeamMemberViewModel> Members { get; set; } = [];

    /// <summary>
    /// Every active member across all pages: Members is paginated, so it can't drive the picker's exclusion.
    /// </summary>
    public List<Guid> AllMemberUserIds { get; set; } = [];

    public List<TeamJoinRequestViewModel> PendingRequests { get; set; } = [];
    public bool CanManageRoles { get; set; }
    public bool CanProvisionEmails { get; set; }

    /// <summary>Active only.</summary>
    public List<ResourceAccessViewModel> TeamResources { get; set; } = [];

    /// <summary>Active only; shown separately on a sub-team.</summary>
    public List<ResourceAccessViewModel> ParentDepartmentResources { get; set; } = [];

    public string? ParentDepartmentName { get; set; }

    public string? ParentDepartmentSlug { get; set; }

    public bool IsSensitive { get; set; }

    /// <summary>For the audit preview in the sensitive-team modal.</summary>
    public Guid ActorUserId { get; set; }
}

internal sealed class ResourceAccessViewModel
{
    public string Name { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string? PermissionLevel { get; set; }
    public string? Url { get; set; }
    public string IconClass { get; set; } = string.Empty;
}

internal sealed class BirthdayCalendarViewModel
{
    public List<BirthdayEntryViewModel> Birthdays { get; set; } = [];
    public int CurrentMonth { get; set; }
    public string CurrentMonthName { get; set; } = string.Empty;
}

internal sealed class BirthdayEntryViewModel
{
    public Guid UserId { get; set; }
    public int DayOfMonth { get; set; }
    public int Month { get; set; }
    public string MonthName { get; set; } = string.Empty;
    public List<string> TeamNames { get; set; } = [];
}

internal sealed class MapViewModel
{
    public List<MapMarkerViewModel> Markers { get; set; } = [];
}

internal sealed class MapMarkerViewModel
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? ProfilePictureUrl { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? City { get; set; }
    public string? CountryCode { get; set; }
}

internal sealed class ApproveRejectRequestModel
{
    public Guid RequestId { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }
}

internal sealed class AddMemberModel
{
    public Guid UserId { get; set; }
}

internal sealed class TeamRoleDefinitionViewModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SlotCount { get; set; }
    public int? EstimatedHours { get; set; }
    public List<TeamRoleSlotViewModel> Slots { get; set; } = [];
    public int SortOrder { get; set; }
    public bool IsPublic { get; set; } = true;
    public bool IsManagement { get; set; }
    public RolePeriod Period { get; set; }

    public HashSet<Guid> AssignedUserIds { get; set; } = [];

    public static TeamRoleDefinitionViewModel FromSnapshot(
        TeamRoleDefinitionSnapshot d,
        IReadOnlyCollection<TeamMemberViewModel> teamMembers)
    {
        var slots = new List<TeamRoleSlotViewModel>();
        var assignedUserIds = new HashSet<Guid>();

        for (var i = 0; i < d.SlotCount; i++)
        {
            var assignment = d.Assignments.FirstOrDefault(a => a.SlotIndex == i);
            var priority = i < d.Priorities.Count ? d.Priorities[i] : SlotPriority.None;
            if (assignment?.AssignedUserId is Guid assignedUserId)
            {
                assignedUserIds.Add(assignedUserId);
            }

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
                AssignedUserId = assignment?.AssignedUserId,
                TeamMemberId = assignment?.TeamMemberId
            });
        }

        return new TeamRoleDefinitionViewModel
        {
            Id = d.Id,
            Name = d.Name,
            Description = d.Description,
            SlotCount = d.SlotCount,
            EstimatedHours = d.EstimatedHours,
            Slots = slots,
            SortOrder = d.SortOrder,
            IsPublic = d.IsPublic,
            IsManagement = d.IsManagement,
            Period = d.Period,
            AssignedUserIds = assignedUserIds
        };
    }
}

internal sealed class TeamRoleSlotViewModel
{
    public int SlotIndex { get; set; }
    public SlotPriority Priority { get; set; }
    public string PriorityBadgeClass { get; set; } = string.Empty;
    public bool IsFilled { get; set; }
    public Guid? AssignedUserId { get; set; }
    public Guid? TeamMemberId { get; set; }
}

internal sealed class RoleManagementViewModel
{
    public Guid TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public bool IsSystemTeam { get; set; }
    public bool IsChildTeam { get; set; }
    public bool CanManage { get; set; }
    public bool CanToggleManagement { get; set; }
    public List<TeamRoleDefinitionViewModel> RoleDefinitions { get; set; } = [];
    public List<TeamMemberViewModel> TeamMembers { get; set; } = [];

    /// <summary>
    /// Burner names resolved by the controller: an exception to the no-copied-display-name rule, because option text
    /// can't host a view component.
    /// </summary>
    public List<TeamMemberDropdownItem> MemberOptions { get; set; } = [];
}

internal sealed class TeamMemberDropdownItem
{
    public Guid UserId { get; set; }
    public string BurnerName { get; set; } = string.Empty;
}

internal sealed class CreateRoleDefinitionModel
{
    public string Name { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }
    [BindRequired]
    public int SlotCount { get; set; } = 1;
    public int? EstimatedHours { get; set; }
    public List<string> Priorities { get; set; } = ["None"];
    [BindRequired]
    public int SortOrder { get; set; }
    public bool IsPublic { get; set; } = true;
    public RolePeriod Period { get; set; } = RolePeriod.YearRound;
}

internal sealed class EditRoleDefinitionModel
{
    public string Name { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }
    [BindRequired]
    public int SlotCount { get; set; }
    public int? EstimatedHours { get; set; }
    public List<string> Priorities { get; set; } = [];
    [BindRequired]
    public int SortOrder { get; set; }
    public bool IsPublic { get; set; } = true;
    public bool IsManagement { get; set; }
    public RolePeriod Period { get; set; } = RolePeriod.YearRound;
}

internal sealed class AssignRoleModel
{
    public Guid UserId { get; set; }
}

internal sealed class RosterSummaryViewModel
{
    public List<RosterSlotViewModel> Slots { get; set; } = [];
    public string? PriorityFilter { get; set; }
    public string? StatusFilter { get; set; }
    public string? PeriodFilter { get; set; }
}

internal sealed class RosterSlotViewModel
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

internal sealed class AdminTeamListViewModel
{
    public List<AdminTeamViewModel> Departments { get; set; } = [];
    public List<AdminTeamViewModel> System { get; set; } = [];
    public List<AdminTeamViewModel> Hidden { get; set; } = [];

    public bool HasAnyTeams => Departments.Count + System.Count + Hidden.Count > 0;
}

internal sealed class AdminTeamViewModel
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
    public bool IsHidden { get; set; }
}

internal sealed class TeamResourceLinkViewModel
{
    public string Name { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string IconClass { get; set; } = string.Empty;
}

/// <summary>
/// Not <see cref="TeamMemberViewModel"/>: that shape is shared with the Members and Roles pages, where a legal name would
/// sit permanently empty.
/// </summary>
internal sealed record TeamRosterViewModel(
    string TeamName,
    string TeamSlug,
    IReadOnlyList<TeamRosterRowViewModel> Rows);

/// <summary>No name field: the burner name renders through <c>&lt;vc:human&gt;</c>.</summary>
internal sealed record TeamRosterRowViewModel(Guid UserId, string LegalName, Instant JoinedAt);
