using Humans.Base.Enums;

namespace Humans.Teams.Contracts;

/// <summary>Called after approval, consent and role changes.</summary>
/// <remarks>The hourly sweep is registered by this interface and job id, so the implementing type may move.</remarks>
public interface ISystemTeamSync
{
    Task<SyncReport> ExecuteAsync(CancellationToken cancellationToken = default);
    Task SyncMembershipForUserAsync(
        Guid userId,
        SystemTeamType teamType,
        CancellationToken cancellationToken = default);
    Task SyncBoardTeamAsync(SyncReport? report = null, CancellationToken cancellationToken = default);
}
