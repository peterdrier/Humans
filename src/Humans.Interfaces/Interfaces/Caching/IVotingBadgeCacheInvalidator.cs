using Humans.Application.Architecture;

namespace Humans.Application.Interfaces.Caching;

/// <summary>
/// Cross-cutting invalidator for a single Board member's voting-badge cache
/// entry. Called by the Governance decorator after an approve/reject
/// finalization: each voter who had cast a vote on the application gets their
/// badge invalidated so the new "nothing to vote on" state surfaces.
/// </summary>
[Grandfathered(
    ruleId: "HUM0028",
    justification: "Pre-existing nav-badge cache for governance voting; remains until the count is absorbed into the governance service + caching decorator.",
    since: "2026-05-27",
    issueRef: "nobodies-collective/Humans#805")]
public interface IVotingBadgeCacheInvalidator : IInvalidator
{
    void Invalidate(Guid userId);
}
