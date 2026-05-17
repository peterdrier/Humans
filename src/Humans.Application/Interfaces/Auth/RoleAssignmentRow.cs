using NodaTime;

namespace Humans.Application.Interfaces.Auth;

/// <summary>
/// Compact projection of a single <c>role_assignments</c> row, used as the
/// cached unit in <c>CachingRoleAssignmentService</c>. Only the fields needed
/// to derive active-by-role counts are included; broader read methods continue
/// to pass through to the inner service.
/// </summary>
public sealed record RoleAssignmentRow(
    Guid Id,
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
