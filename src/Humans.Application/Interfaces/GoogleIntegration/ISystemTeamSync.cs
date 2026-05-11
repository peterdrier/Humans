using Humans.Application.DTOs;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.GoogleIntegration;

/// <summary>
/// Syncs system team memberships (Volunteers, Coordinators, Colaboradors, Asociados, Board, Barrio Leads)
/// after approval/consent/role changes.
/// </summary>
public interface ISystemTeamSync
{
    Task<SyncReport> ExecuteAsync(CancellationToken cancellationToken = default);
    Task SyncMembershipForUserAsync(
        Guid userId,
        SystemTeamType teamType,
        CancellationToken cancellationToken = default);
    Task SyncBoardTeamAsync(SyncReport? report = null, CancellationToken cancellationToken = default);
}
