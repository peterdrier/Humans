using System.Security.Claims;
using Humans.Domain.Constants;

namespace Humans.Web.Authorization;

public static class ShiftRoleChecks
{
    public static bool IsPrivilegedSignupApprover(ClaimsPrincipal user)
    {
        return user.IsInRole(RoleNames.Admin) || user.IsInRole(RoleNames.NoInfoAdmin);
    }

    public static bool CanAccessDashboard(ClaimsPrincipal user)
    {
        return IsPrivilegedSignupApprover(user) || user.IsInRole(RoleNames.VolunteerCoordinator);
    }

    public static bool CanViewMedical(ClaimsPrincipal user)
    {
        return IsPrivilegedSignupApprover(user);
    }
}
