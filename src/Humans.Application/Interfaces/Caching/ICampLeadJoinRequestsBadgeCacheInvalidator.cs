using Humans.Application.Architecture;

namespace Humans.Application.Interfaces.Caching;

/// <summary>
/// Cross-cutting invalidator for a camp lead's "humans waiting to join" badge
/// cache entry. Called by <c>CampService</c> after any CampMember status
/// transition that changes the Pending count: request, approve, reject,
/// withdraw, leave, remove, and cascading season rejection/withdrawal. Each
/// active lead of the affected camp gets their badge invalidated.
/// </summary>
[Grandfathered(
    ruleId: "HUM0028",
    justification: "Pre-existing per-user nav-badge cache for camp-lead join requests; remains until the count is absorbed into Camps' service + caching decorator.",
    since: "2026-05-27",
    issueRef: "nobodies-collective/Humans#805")]
public interface ICampLeadJoinRequestsBadgeCacheInvalidator : IInvalidator
{
    void Invalidate(Guid userId);
}
