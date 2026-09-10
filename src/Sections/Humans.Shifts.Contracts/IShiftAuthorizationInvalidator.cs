using Humans.Base.Interfaces;

using Humans.Base.Attributes;


namespace Humans.Shifts.Contracts;

/// <summary>
/// One-way cache-staleness signal for the per-user shift-authorization cache
/// (<c>shift-auth:{userId}</c>, 60s TTL) owned by
/// <c>IShiftManagementService</c>. Implemented by ShiftManagementService.
/// External sections that change the
/// user's team / coordinator / admin state inject this and call
/// <see cref="Invalidate"/> after their own writes — they never mutate the
/// Shifts cache directly.
/// </summary>
[Grandfathered(
    ruleId: "HUM0028",
    justification: "Pre-existing shift-authorization cache flushed cross-section (deletion cascade); remains until shift-auth caching is absorbed by the owning service.",
    since: "2026-05-27",
    issueRef: "nobodies-collective/Humans#805")]
public interface IShiftAuthorizationInvalidator : IInvalidator
{
    /// <summary>
    /// Drops the cached coordinator-team-id list for a single user. The next
    /// call to <c>IShiftManagementServiceRead.GetCoordinatorTeamIdsAsync</c>
    /// for that user re-queries via <c>ITeamService</c>.
    /// </summary>
    void Invalidate(Guid userId);
}
