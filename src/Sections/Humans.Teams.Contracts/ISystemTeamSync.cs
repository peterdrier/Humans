using Humans.Domain.Enums;

namespace Humans.Teams.Contracts;

/// <summary>
/// Syncs system team memberships (Volunteers, Coordinators, Colaboradors, Asociados, Board, Barrio Leads)
/// after approval/consent/role changes.
/// </summary>
/// <remarks>
/// System-team membership is a Teams invariant and every write the implementation
/// (<c>Humans.Teams/Services/SystemTeamSyncJob</c>) makes lands in Teams' own tables, so the
/// contract belongs on this leaf. It sat in <c>Humans.Application.Interfaces.GoogleIntegration</c>
/// until G5 lane 5c (nobodies-collective/Humans#866) on the belief that Hangfire pinned its
/// assembly-qualified name; the hourly sweep is registered with
/// <c>RecurringJob.AddOrUpdate&lt;ISystemTeamSync&gt;(id, …)</c>, which is keyed on the id and
/// rewritten at every startup, so the stored type string follows the move at boot.
/// </remarks>
public interface ISystemTeamSync
{
    Task<SyncReport> ExecuteAsync(CancellationToken cancellationToken = default);
    Task SyncMembershipForUserAsync(
        Guid userId,
        SystemTeamType teamType,
        CancellationToken cancellationToken = default);
    Task SyncBoardTeamAsync(SyncReport? report = null, CancellationToken cancellationToken = default);
}
