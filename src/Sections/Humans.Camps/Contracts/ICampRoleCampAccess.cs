namespace Humans.Camps.Contracts;

/// <summary>
/// Narrow camp-side port for camp role workflows. Implemented only by the caching
/// camp service so role migrations still pass through cache invalidation.
/// </summary>
internal interface ICampRoleCampAccess
{
    Task<CampSettingsInfo> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task<CampMemberLookup?> GetCampMemberStatusAsync(Guid campMemberId, CancellationToken cancellationToken = default);

    /// <summary>
    /// One row per camp season participating in <paramref name="year"/>.
    /// <c>JoinedMemberCount</c> counts active members.
    /// </summary>
    Task<IReadOnlyList<(Guid CampId, string CampName, string CampSlug, Guid CampSeasonId,
            CampSeasonStatus Status, int TargetMemberCount, int? JoinedMemberCount)>>
        GetCampSeasonsForComplianceAsync(int year, CancellationToken cancellationToken = default);
}
