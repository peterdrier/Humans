using Humans.Application.DTOs;
using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.GoogleIntegration;

/// <summary>
/// Syncs system team memberships (Volunteers, Coordinators, Colaboradors, Asociados, Board, Barrio Leads)
/// after approval/consent/role changes.
/// </summary>
/// <remarks>
/// The only file left in this folder after GoogleIntegration's G5 move
/// (nobodies-collective/Humans#866), and deliberately so. It names Google nowhere in its
/// signature, and its implementation — <c>Humans.Infrastructure/Jobs/SystemTeamSyncJob</c> —
/// injects Auth, AuditLog, Email, Governance, Teams and Users, which makes it a cross-section
/// orchestrator rather than a Google service. Putting it on
/// <c>Humans.GoogleIntegration.Contracts</c> would have made Auth — a horizontal — reference
/// a vertical section, which <c>peters-hard-rules.md</c> forbids; leaving it here keeps Auth,
/// Development, Governance and Onboarding off that leaf entirely. Its permanent home is
/// lane 4's question (PR peterdrier/Humans#1291), not this section's.
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
