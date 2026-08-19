using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Camps;

/// <summary>
/// Camps' authorization policies, at the project root by convention. Discovered by Shell
/// alongside <see cref="Section"/> — nothing names it.
/// </summary>
/// <remarks>
/// <c>CampComplianceAccess</c> stays in Shell: it also admits any team/sub-team coordinator
/// (a Shifts-domain lookup), so it is a composite spanning two sections' roles.
/// </remarks>
internal sealed class SectionPolicies : ISectionPolicies
{
    public void AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(PolicyNames.CampAdminOrAdmin, policy =>
            policy.RequireRole(RoleNames.CampAdmin, RoleNames.Admin));
    }
}
