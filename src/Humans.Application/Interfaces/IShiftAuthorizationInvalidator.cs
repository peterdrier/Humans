namespace Humans.Application.Interfaces;

/// <summary>
/// One-way cache-staleness signal for the per-user shift-authorization cache
/// (<c>shift-auth:{userId}</c>, 60s TTL) owned by
/// <see cref="IShiftManagementService"/>. Implemented by the Shifts service
/// itself in Infrastructure/Application. External sections that change the
/// user's team / coordinator / admin state inject this and call
/// <see cref="Invalidate"/> after their own writes — they never mutate the
/// Shifts cache directly.
///
/// <para>
/// Added during the Shifts §15 migration (issue #541a) to close the gap
/// recorded in design-rules §15 NEW-B: the Profile <c>RequestDeletionAsync</c>
/// path used to clear this cache via the old bundled <c>InvalidateUserCaches</c>
/// extension, but the §15 Profile migration left the shift-auth entry stale
/// until the 60s TTL elapsed. Plumbing the invalidator through
/// <see cref="IShiftManagementService"/> restores cross-section invalidation
/// without re-introducing the <c>IMemoryCache</c> fan-out.
/// </para>
/// </summary>
public interface IShiftAuthorizationInvalidator
{
    /// <summary>
    /// Drops the cached coordinator-team-id list for a single user. The next
    /// call to <see cref="IShiftManagementService.GetCoordinatorTeamIdsAsync"/>
    /// for that user re-queries via <see cref="ITeamService"/>.
    /// </summary>
    void Invalidate(Guid userId);
}
