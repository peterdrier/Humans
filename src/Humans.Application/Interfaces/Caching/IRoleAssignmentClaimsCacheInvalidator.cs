namespace Humans.Application.Interfaces.Caching;

/// <summary>
/// Cross-cutting invalidator for the per-user role-assignment claims cache
/// consumed by <c>RoleAssignmentClaimsTransformation</c>. Owned by the Auth
/// section. Application-layer services (Auth itself, account merge, onboarding,
/// etc.) call <see cref="Invalidate"/> after writing role changes so the next
/// request for that user re-derives claims from the DB instead of serving the
/// 60-second cached snapshot.
/// </summary>
public interface IRoleAssignmentClaimsCacheInvalidator
{
    void Invalidate(Guid userId);
}
