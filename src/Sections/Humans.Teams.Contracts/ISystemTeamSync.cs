using Humans.Base.Enums;

namespace Humans.Teams.Contracts;

/// <summary>Called after consent, team and camp-lead changes.</summary>
/// <remarks>The hourly sweep is registered by this interface and job id, so the implementing type may move.</remarks>
public interface ISystemTeamSync
{
    Task<SyncReport> ExecuteAsync(CancellationToken cancellationToken = default);
    Task SyncMembershipForUserAsync(
        Guid userId,
        SystemTeamType teamType,
        CancellationToken cancellationToken = default);
}
