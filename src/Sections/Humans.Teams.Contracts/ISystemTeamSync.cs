using Humans.Base.Enums;

namespace Humans.Teams.Contracts;

/// <summary>
/// Syncs system team memberships (Volunteers, Coordinators, Colaboradors, Asociados, Board, Barrio Leads)
/// after approval/consent/role changes.
/// </summary>
/// <remarks>
/// System-team membership is a Teams invariant and every write the implementation
/// (<c>SystemTeamSyncJob</c>) makes lands in Teams' own tables, so the contract lives on
/// this leaf. The hourly sweep is registered with
/// <c>RecurringJob.AddOrUpdate&lt;ISystemTeamSync&gt;(id, …)</c>, keyed on the id, so the
/// implementing type may move freely.
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
