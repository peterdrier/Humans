using Humans.Application.DTOs;

namespace Humans.Application.Interfaces.GoogleIntegration;

/// <summary>
/// Syncs system team memberships (Volunteers, Coordinators, Colaboradors, Asociados, Board, Barrio Leads)
/// after approval/consent/role changes.
/// </summary>
public interface ISystemTeamSync
{
    Task<SyncReport> ExecuteAsync(CancellationToken cancellationToken = default);
    Task SyncVolunteersMembershipForUserAsync(Guid userId, CancellationToken cancellationToken = default);
    Task SyncCoordinatorsMembershipForUserAsync(Guid userId, CancellationToken cancellationToken = default);
    Task SyncColaboradorsMembershipForUserAsync(Guid userId, CancellationToken cancellationToken = default);
    Task SyncAsociadosMembershipForUserAsync(Guid userId, CancellationToken cancellationToken = default);
    Task SyncBoardTeamAsync(SyncReport? report = null, CancellationToken cancellationToken = default);
    Task SyncBarrioLeadsMembershipForUserAsync(Guid userId, CancellationToken cancellationToken = default);
}
