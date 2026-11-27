using System.Security.Claims;
using Humans.Base.Authorization;

namespace Humans.Shifts.Contracts;

/// <summary>
/// Shifts' grantable roles and the predicates over them. In Contracts because Teams' team page asks
/// <see cref="CanManageDepartment"/> and Users' profile page asks <see cref="IsPrivilegedSignupApprover"/>.
/// </summary>
public static class ShiftsRoles
{
    /// <summary>
    /// Shifts Admin: system-wide shift management (rotas, departments, approvals) and the facilitation contact for
    /// onboarding humans. Stored as <c>VolunteerCoordinator</c> before 2026-10.
    /// </summary>
    public const string ShiftsAdmin = "ShiftsAdmin";

    public static bool IsPrivilegedSignupApprover(ClaimsPrincipal user) =>
        RoleChecks.IsAdmin(user) || user.IsInRole(ShiftsAdmin);

    public static bool CanManageDepartment(ClaimsPrincipal user) => IsPrivilegedSignupApprover(user);

    public static bool IsVolunteerManager(ClaimsPrincipal user) =>
        RoleChecks.IsAdmin(user) || user.IsInRole(ShiftsAdmin);
}
