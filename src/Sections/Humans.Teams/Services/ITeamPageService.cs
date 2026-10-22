using Humans.GoogleIntegration.Contracts;
using Humans.Base.Interfaces;
using Humans.Teams.Contracts;
using Humans.Base.Enums;
using NodaTime;

namespace Humans.Teams.Services;

internal sealed record TeamDirectorySummary(
    Guid Id,
    string Name,
    string? Description,
    string Slug,
    int MemberCount,
    bool IsSystemTeam,
    bool IsHidden,
    bool RequiresApproval,
    bool IsPublicPage,
    bool IsCurrentUserMember,
    bool IsCurrentUserCoordinator,
    string? ParentTeamName,
    string? ParentTeamSlug)
{
    public string SortKey => ParentTeamName is not null ? $"{ParentTeamName} - {Name}" : Name;
}

internal sealed record TeamDirectoryResult(
    bool IsAuthenticated,
    bool CanCreateTeam,
    IReadOnlyList<TeamDirectorySummary> MyTeams,
    IReadOnlyList<TeamDirectorySummary> Departments,
    IReadOnlyList<TeamDirectorySummary> SystemTeams,
    IReadOnlyList<TeamDirectorySummary> HiddenTeams);

internal sealed record TeamRosterSlotSummary(
    string TeamName,
    string TeamSlug,
    string RoleName,
    string? RoleDescription,
    Guid RoleDefinitionId,
    int SlotNumber,
    string Priority,
    string PriorityBadgeClass,
    string Period,
    bool IsFilled,
    Guid? AssignedUserId,
    string? AssignedUserName,
    bool IsPublic);

internal sealed record MyTeamMembershipSummary(
    Guid TeamId,
    string TeamName,
    string TeamSlug,
    bool IsSystemTeam,
    TeamMemberRole Role,
    Instant JoinedAt,
    bool CanLeave,
    int PendingRequestCount);

internal sealed record TeamDetailMemberSummary(
    Guid UserId,
    string DisplayName,
    string? Email,
    string? ProfilePictureUrl,
    TeamMemberRole Role,
    Instant JoinedAt);

internal sealed record TeamDetailResult(
    TeamPageTeamSummary Team,
    IReadOnlyList<TeamDetailMemberSummary> Members,
    IReadOnlyList<TeamPageTeamLink> ChildTeams,
    IReadOnlyList<TeamRoleDefinitionSnapshot> RoleDefinitions,
    bool IsAuthenticated,
    bool IsCurrentUserMember,
    bool IsCurrentUserCoordinator,
    bool CanCurrentUserJoin,
    bool CanCurrentUserLeave,
    bool CanCurrentUserManage,
    bool CanCurrentUserEditTeam,
    Guid? CurrentUserPendingRequestId,
    int PendingRequestCount);

internal sealed record TeamPageMemberSummary(
    Guid UserId,
    string DisplayName,
    string? Email,
    string? ProfilePictureUrl,
    TeamMemberRole Role,
    Instant? JoinedAt);

internal sealed record TeamPageResourceSummary(
    string Name,
    string Url,
    GoogleResourceType ResourceType,
    Instant ProvisionedAt);

internal sealed record TeamPageShiftsSummary(
    int TotalSlots,
    int ConfirmedCount,
    int PendingCount,
    int UniqueVolunteerCount,
    bool CanManageShifts,
    int IncludesSubTeamCount = 0);

internal sealed record TeamPageTeamLink(Guid Id, string Name, string Slug);

/// <summary>
/// A child-team coordinator, or a member not already directly in the department. <see cref="RoleTitle"/> is set for
/// coordinators only.
/// </summary>
internal sealed record TeamPageChildTeamMemberSummary(
    Guid UserId,
    string ChildTeamName,
    string ChildTeamSlug,
    bool IsCoordinator,
    string? RoleTitle);

internal sealed record TeamPageTeamSummary(
    Guid Id,
    string Name,
    string DisplayName,
    string? Description,
    string Slug,
    bool IsActive,
    bool RequiresApproval,
    bool IsSystemTeam,
    SystemTeamType SystemTeamType,
    Instant CreatedAt,
    bool IsPublicPage,
    bool ShowCoordinatorsOnPublicPage,
    string? PageContent,
    List<CallToAction> CallsToAction,
    Instant? PageContentUpdatedAt,
    Guid? PageContentUpdatedByUserId,
    TeamPageTeamLink? ParentTeam);

internal sealed record TeamPageDetailResult(
    TeamPageTeamSummary Team,
    IReadOnlyList<TeamPageMemberSummary> Members,
    IReadOnlyList<TeamPageTeamLink> ChildTeams,
    IReadOnlyList<TeamRoleDefinitionSnapshot> RoleDefinitions,
    IReadOnlyList<TeamPageResourceSummary> Resources,
    bool IsAuthenticated,
    bool IsCurrentUserMember,
    bool IsCurrentUserCoordinator,
    bool CanCurrentUserJoin,
    bool CanCurrentUserLeave,
    bool CanCurrentUserManage,
    bool CanCurrentUserEditTeam,
    Guid? CurrentUserPendingRequestId,
    int PendingRequestCount,
    string? PageContentUpdatedByDisplayName,
    TeamPageShiftsSummary? ShiftsSummary,
    IReadOnlyList<TeamPageChildTeamMemberSummary> SubteamLeads,
    IReadOnlyList<TeamPageChildTeamMemberSummary> SubteamMembers);

internal interface ITeamPageService : IApplicationService
{
    Task<TeamDirectoryResult> GetTeamDirectoryAsync(Guid? userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TeamRosterSlotSummary>> GetRosterAsync(
        string? priority,
        string? status,
        string? period,
        CancellationToken cancellationToken = default);

    /// <summary>With viewer-specific pending-request counts.</summary>
    Task<IReadOnlyList<MyTeamMembershipSummary>> GetMyTeamMembershipsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<TeamPageDetailResult?> GetTeamPageDetailAsync(
        string slug,
        Guid? userId,
        bool canManageShiftsByRole,
        CancellationToken cancellationToken = default);
}
