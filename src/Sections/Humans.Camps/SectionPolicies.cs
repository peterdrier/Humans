using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.Camps.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Camps;

/// <summary>
/// Camps' authorization policies, at the project root by convention. Discovered by Shell
/// alongside <see cref="Section"/> — nothing names it.
/// </summary>
/// <remarks>
/// <c>CampComplianceAccess</c> registers here — not in Shifts, whose coordinator lookup
/// its handler reads — because this section is the policy's consumer
/// (<c>CampComplianceController</c>, the Compliance nav entry) and the derived
/// section-dependency graph can't see a policy-name reference: a Shifts-owned
/// registration could leave Camps 500ing "policy not found" if Shifts were ever
/// deactivated alone. Registered from Camps, the policy leaves with its consumers
/// (nobodies-collective/Humans#1091).
/// </remarks>
internal sealed class SectionPolicies : ISectionPolicies
{
    public void AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(PolicyNames.CampAdminOrAdmin, policy =>
            policy.RequireRole(RoleNames.CampAdmin, RoleNames.Admin));

        // CampAdmin/Admin OR any team coordinator — the OR (including the
        // team-coordinator lookup) lives in Shifts' CampComplianceAccessHandler so the
        // policy is a single requirement (policy requirements AND together).
        options.AddPolicy(PolicyNames.CampComplianceAccess, policy =>
            policy.AddRequirements(new CampComplianceAccessRequirement()));
    }
}
