using Humans.Application.Architecture;

namespace Humans.Application.Interfaces.Caching;

/// <summary>
/// Cross-section signal for the global role-assignment cache. Implemented
/// by the Singleton <c>CachingRoleAssignmentService</c> decorator and
/// consumed by the <c>RoleAssignmentSaveChangesInterceptor</c>, which
/// fires after EF persists any write to <c>role_assignments</c>.
/// </summary>
/// <remarks>
/// Bag-shaped cache (whole-set replacement on rebuild) — same pattern as
/// <see cref="ILegalDocumentCacheInvalidator"/>. The cached unit is the
/// full set of role-assignment rows; per-row invalidation is not used
/// because writes are rare and a wholesale reload is cheap.
///
/// Distinct from <see cref="IRoleAssignmentClaimsCacheInvalidator"/>:
/// the claims invalidator targets per-user authentication claims (cookie /
/// per-request roles), while this invalidator targets the singleton list
/// cache that backs cross-section reads such as
/// <c>IRoleAssignmentService.GetActiveCountsByRoleAsync</c>.
/// </remarks>
[Grandfathered(
    ruleId: "HUM0028",
    justification: "Pre-existing role-assignment cache flushed by cross-section role writes; remains until RoleAssignmentService's caching decorator owns invalidation end-to-end.",
    since: "2026-05-27",
    issueRef: "nobodies-collective/Humans#805")]
public interface IRoleAssignmentCacheInvalidator : IInvalidator
{
    /// <summary>
    /// Evict the entire role-assignment cache. Next read repopulates lazily.
    /// </summary>
    void InvalidateAll();
}
