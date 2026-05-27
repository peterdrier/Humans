using Humans.Application.Architecture;

namespace Humans.Application.Interfaces.Caching;

/// <summary>
/// Per-user invalidator for the cached actionable-issues count surfaced by the
/// nav-badge view component. Called by <c>IssuesService</c> after a mutation
/// that may shift the actionable count seen by some set of users (the
/// reporter, admins, and role-holders for the affected section).
/// </summary>
[Grandfathered(
    ruleId: "HUM0028",
    justification: "Pre-existing cross-section nav-badge cache; remains until actionable-issues count is absorbed into the owning section's service + caching decorator.",
    since: "2026-05-27",
    issueRef: "nobodies-collective/Humans#805")]
public interface IIssuesBadgeCacheInvalidator : IInvalidator
{
    void Invalidate(Guid userId);
    void InvalidateMany(IEnumerable<Guid> userIds);
}
