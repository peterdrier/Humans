namespace Humans.Application.Interfaces.Caching;

/// <summary>
/// Per-user invalidator for the cached actionable-issues count surfaced by the
/// nav-badge view component. Called by <c>IssuesService</c> after a mutation
/// that may shift the actionable count seen by some set of users (the
/// reporter, admins, and role-holders for the affected section).
/// </summary>
public interface IIssuesBadgeCacheInvalidator
{
    void Invalidate(Guid userId);
    void InvalidateMany(IEnumerable<Guid> userIds);
}
