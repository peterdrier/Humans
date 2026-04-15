namespace Humans.Application.Interfaces.Caching;

/// <summary>
/// Cross-cutting invalidator for a single Board member's voting-badge cache
/// entry. Called by the Governance decorator after an approve/reject
/// finalization: each voter who had cast a vote on the application gets their
/// badge invalidated so the new "nothing to vote on" state surfaces.
/// </summary>
public interface IVotingBadgeCacheInvalidator
{
    void Invalidate(Guid userId);
}
