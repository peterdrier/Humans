using System.Security.Claims;
using Humans.Base.Constants;

namespace Humans.Base.Authorization;

/// <summary>
/// Role predicates for the volunteer-shift surface. In Base because <c>Humans.Teams</c>' team page asks
/// <see cref="CanManageDepartment"/> and a section cannot reference <c>Humans.Web</c>.
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

    public static bool CanViewMedical(ClaimsPrincipal user)
    {
        return IsPrivilegedSignupApprover(user);
    }
}
