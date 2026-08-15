using Humans.Application.Architecture;

namespace Humans.Application.Interfaces.Caching;

/// <summary>
/// Cross-cutting invalidator for the per-user role-assignment claims cache
/// consumed by <c>RoleAssignmentClaimsTransformation</c>. Owned by the Auth
/// section. Application-layer services (Auth itself, account merge, onboarding,
/// etc.) call <see cref="Invalidate"/> after writing role changes so the next
/// request for that user re-derives claims from the DB instead of serving the
/// 60-second cached snapshot.
/// </summary>
[Grandfathered(
    ruleId: "HUM0028",
    justification: "Pre-existing claims-transformation cache flushed by deletion + role writes; remains until claims transformation cache is owned by an Auth-section service.",
    since: "2026-05-27",
    issueRef: "nobodies-collective/Humans#805")]
public interface IRoleAssignmentClaimsCacheInvalidator : IInvalidator
{
    void Invalidate(Guid userId);
}
