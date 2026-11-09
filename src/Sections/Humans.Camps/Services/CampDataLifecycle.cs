using Humans.Base.Enums;
using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Camps.Services;

internal sealed class CampDataLifecycle(
    ICampRepository _repo,
    IUserService _userService,
    ISystemTeamSync _systemTeamSync) : IApplicationService
{
    /// <summary>GDPR export JSON key for this contributor's data.</summary>
    internal const string CampRoleAssignments = "CampRoleAssignments";
    internal const string CampMemberships = "CampMemberships";

    internal async Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken ct)
    {
        // Called from AccountMergeService.MergeAsync's ordered fan-out; must stay idempotent.
        await _repo.ReassignMembershipsToUserAsync(sourceUserId, targetUserId, updatedAt, ct);

        // Lead moves change Barrio Leads team membership for both users.
        await _systemTeamSync.SyncMembershipForUserAsync(sourceUserId, SystemTeamType.BarrioLeads, ct);
        await _systemTeamSync.SyncMembershipForUserAsync(targetUserId, SystemTeamType.BarrioLeads, ct);
    }

    internal async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        userId = (await _userService.GetUserInfoAsync(userId, ct))?.Id ?? userId;
        var roleAssignments = await _repo.GetAllAssignmentsForUserAsync(userId, ct);

        var shapedRoles = roleAssignments.Select(a => new
        {
            CampSlug = a.CampSeason.Camp.Slug,
            SeasonYear = a.CampSeason.Year,
            RoleName = a.Definition.Name,
            AssignedAt = a.AssignedAt.ToIso8601(),
            a.AssignedByUserId
        }).ToList();

        var memberships = await _repo.GetAllMembershipsForUserAsync(userId, ct);
        var shapedMemberships = memberships.Select(m => new
        {
            m.Id,
            m.CampSeasonId,
            CampSlug = m.CampSeason.Camp.Slug,
            SeasonYear = m.CampSeason.Year,
            Status = m.Status.ToString(),
            RequestedAt = m.RequestedAt.ToIso8601(),
            ConfirmedAt = m.ConfirmedAt?.ToIso8601(),
            m.ConfirmedByUserId,
            RemovedAt = m.RemovedAt?.ToIso8601(),
            m.RemovedByUserId,
            m.HasEarlyEntry
        }).ToList();

        return [new UserDataSlice(CampRoleAssignments, shapedRoles), new UserDataSlice(CampMemberships, shapedMemberships)];
    }

    internal static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [CampRoleAssignments] = null,
            [CampMemberships] = null
        };

    /// <summary>
    /// Drops the user's camp memberships and every role assignment hanging off
    /// them (Camp Lead included — it is a role assignment).
    /// </summary>
    internal async Task EraseForUserAsync(Guid userId, CancellationToken ct)
    {
        await _repo.DeleteCampFootprintForUserAsync(userId, ct);
        // The cached CampInfo rosters this just emptied are dropped by CachingCampService,
        // the registered IUserDataContributor, which refreshes after this returns.
    }
}
