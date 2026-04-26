namespace Humans.Application.Interfaces.Caching;

/// <summary>
/// Cross-cutting invalidator for a camp lead's "humans waiting to join" badge
/// cache entry. Called by <c>CampService</c> after any CampMember status
/// transition that changes the Pending count: request, approve, reject,
/// withdraw, leave, remove, and cascading season rejection/withdrawal. Each
/// active lead of the affected camp gets their badge invalidated.
/// </summary>
public interface ICampLeadJoinRequestsBadgeCacheInvalidator
{
    void Invalidate(Guid userId);
}
