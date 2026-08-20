using System.Security.Claims;
using Humans.Base.Constants;

namespace Humans.Base.Authorization;

/// <summary>
/// Role predicates for the volunteer-shift surface. In <c>Humans.Base</c> beside
/// <see cref="RoleChecks"/> rather than in Shell because <c>Humans.Teams</c>' team page asks
/// <see cref="CanManageDepartment"/> before rendering the shifts card and a section cannot
/// reference <c>Humans.Web</c>; they read role names off a <see cref="ClaimsPrincipal"/> and
/// name no section vocabulary, which is the test (design §15 step 6).
/// </summary>
public static class ShiftRoleChecks
{
    public static bool IsPrivilegedSignupApprover(ClaimsPrincipal user)
    {
        return user.IsInRole(RoleNames.Admin) || user.IsInRole(RoleNames.NoInfoAdmin);
    }

    public static bool CanManageDepartment(ClaimsPrincipal user)
    {
        return IsPrivilegedSignupApprover(user) || user.IsInRole(RoleNames.VolunteerCoordinator);
    }

    public static bool CanAccessDashboard(ClaimsPrincipal user)
    {
        return CanManageDepartment(user);
    }

    public static bool CanViewMedical(ClaimsPrincipal user)
    {
        return IsPrivilegedSignupApprover(user);
    }
}
