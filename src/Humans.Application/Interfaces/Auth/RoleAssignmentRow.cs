using NodaTime;

namespace Humans.Application.Interfaces.Auth;

/// <summary>
/// Compact projection of a single <c>role_assignments</c> row, used as the
/// cached unit in <c>CachingRoleAssignmentService</c>. Carries the fields
/// needed to derive both active-by-role counts and active-for-user lookups
/// in memory.
/// </summary>
public sealed record RoleAssignmentRow(
    Guid Id,
    Guid UserId,
    string RoleName,
    Instant ValidFrom,
    Instant? ValidTo)
{
    /// <summary>
    /// Canonical "is this assignment active at <paramref name="now"/>?" predicate.
    /// Lives on the row record (design-rules §15d) so the caching decorator
    /// doesn't reimplement the business rule inline.
    /// </summary>
    public bool IsActiveAt(Instant now) =>
        ValidFrom <= now && (ValidTo is null || ValidTo > now);
}
