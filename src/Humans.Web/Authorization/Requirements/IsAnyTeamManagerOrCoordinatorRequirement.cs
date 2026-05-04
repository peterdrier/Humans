using Microsoft.AspNetCore.Authorization;

namespace Humans.Web.Authorization.Requirements;

/// <summary>
/// Succeeds when the user is a team coordinator OR holds a management role
/// (<c>TeamRoleDefinition.IsManagement == true</c>) on any non-system team or
/// sub-team. Used in policies that gate "anyone with team responsibility"
/// surfaces — currently the wider Shifts dashboard entry point — without
/// granting them the privileged sub-panels that stay behind the role-based
/// <see cref="PolicyNames.ShiftDashboardAccess"/>.
/// </summary>
public class IsAnyTeamManagerOrCoordinatorRequirement : IAuthorizationRequirement;
