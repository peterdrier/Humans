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
    Instant? ValidTo);
