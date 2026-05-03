using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Succeeds when the user is a coordinator or holds a management role on any
/// team or sub-team in the system. Used in policies that gate "anyone with
/// team responsibility" surfaces — currently the wider Shifts dashboard
/// entry point — without granting them the privileged sub-panels that stay
/// behind the role-based <see cref="PolicyNames.ShiftDashboardAccess"/>.
/// </summary>
public class IsAnyTeamCoordinatorRequirement : IAuthorizationRequirement;
