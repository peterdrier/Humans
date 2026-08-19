using Humans.Base.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Shifts.Contracts;

/// <summary>
/// Succeeds when the user is a team coordinator OR holds a management role
/// (<c>TeamRoleDefinition.IsManagement == true</c>) on any non-system team or
/// sub-team. Used in policies that gate "anyone with team responsibility"
/// surfaces — currently the wider Shifts dashboard entry point — without
/// granting them the privileged sub-panels that stay behind the role-based
/// <see cref="PolicyNames.ShiftDashboardAccess"/>. Lives under Contracts/
/// (unlike the internal handler) because Shell's AuthorizationPolicyExtensions
/// constructs it directly to back the policy.
/// </summary>
public class IsAnyTeamManagerOrCoordinatorRequirement : IAuthorizationRequirement;
