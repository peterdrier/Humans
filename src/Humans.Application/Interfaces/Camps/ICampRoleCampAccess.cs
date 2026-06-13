using Humans.Domain.Enums;

namespace Humans.Application.Interfaces.Camps;

/// <summary>
/// Narrow camp-side port for camp role workflows. Implemented by the caching
/// camp service so role migrations still pass through cache invalidation.
/// </summary>
public interface ICampRoleCampAccess
{
    Task<CampSettingsInfo> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task<CampMemberLookup?> GetCampMemberStatusAsync(Guid campMemberId, CancellationToken cancellationToken = default);

    /// <summary>
    /// One row per camp season participating in <paramref name="year"/>.
    /// <c>JoinedMemberCount</c> (active members) is only available from the cached
    /// read model; the uncached implementation returns <c>null</c>.
    /// </summary>
    Task<IReadOnlyList<(Guid CampId, string CampName, string CampSlug, Guid CampSeasonId,
            CampSeasonStatus Status, int TargetMemberCount, int? JoinedMemberCount)>>
        GetCampSeasonsForComplianceAsync(int year, CancellationToken cancellationToken = default);

    /// <summary>
    /// Idempotent migration helper: ensures a <c>CampMember</c>(<c>Status = Active</c>)
    /// row exists for the given season and user, promoting Pending when needed.
    /// </summary>
    Task<Guid> EnsureActiveMemberForMigrationAsync(
        Guid campSeasonId, Guid userId, Guid actorUserId,
        CancellationToken cancellationToken = default);
}
