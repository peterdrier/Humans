using Humans.Application.DTOs;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.GoogleIntegration;

/// <summary>
/// Syncs system team memberships (Volunteers, Coordinators, Colaboradors, Asociados, Board, Barrio Leads)
/// after approval/consent/role changes.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type is pinned and must not move.</b> Hangfire serializes the declaring type name of
/// a job target, and Shell registers the hourly sweep as
/// <c>RecurringJob.AddOrUpdate&lt;ISystemTeamSync&gt;</c>
/// (<c>Humans.Web/Extensions/RecurringJobExtensions.cs</c>). Changing its namespace or its
/// assembly makes any job already queued or retry-delayed at deploy time unresolvable — it
/// fails instead of running, and neither the build nor the test suite says so.
/// </para>
/// <para>
/// The implementation lives in the section that owns the invariant:
/// <c>Humans.Teams/Services/SystemTeamSyncJob</c> (G5 lane 4b-2e,
/// nobodies-collective/Humans#866). System-team membership is a Teams invariant, every write
/// the job makes lands in Teams' own tables, and Hangfire resolves the implementation from DI
/// at execution time, so only the interface is constrained. The interface names Google nowhere
/// and sits in this folder for the Hangfire reason alone; it is the one G5 residue left in
/// <c>Humans.Application</c> for a production-safety reason rather than an unfinished move.
/// Retiring it needs a deploy-time queue-drain plan, not a lane.
/// </para>
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
