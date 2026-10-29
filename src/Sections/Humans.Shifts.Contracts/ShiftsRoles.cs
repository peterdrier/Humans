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

    /// <summary>
    /// Shifts NoInfo: approves and voluntells shift signups but does not create or edit shifts; may reveal volunteer
    /// medical data. Stored as <c>NoInfoAdmin</c> before 2026-10.
    /// </summary>
    public const string ShiftsNoInfo = "ShiftsNoInfo";

    public static bool IsPrivilegedSignupApprover(ClaimsPrincipal user) =>
        RoleChecks.IsAdmin(user) || user.IsInRole(ShiftsNoInfo);

    public static bool CanManageDepartment(ClaimsPrincipal user) =>
        IsPrivilegedSignupApprover(user) || user.IsInRole(ShiftsAdmin);

    /// <summary>Who may open a member's medical conditions through the audited reveal; mirrors <c>MedicalDataViewer</c>.</summary>
    public static bool CanViewMedical(ClaimsPrincipal user) =>
        IsPrivilegedSignupApprover(user) || RoleChecks.IsBoard(user);

    /// <summary>Admin or Shifts Admin; deliberately excludes Shifts NoInfo, who approves signups but manages no rotas.</summary>
    public static bool IsVolunteerManager(ClaimsPrincipal user) =>
        RoleChecks.IsAdmin(user) || user.IsInRole(ShiftsAdmin);
}
