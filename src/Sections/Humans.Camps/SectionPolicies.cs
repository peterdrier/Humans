using Humans.Camps.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Camps.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Camps;

/// <summary>
/// <c>CampComplianceAccess</c> registers here, not in Shifts, because the section-dependency graph can't see a
/// policy-name reference: a Shifts-owned registration would leave Camps 500ing "policy not found" if Shifts were
/// deactivated alone.
/// </summary>
internal sealed class SectionPolicies : ISectionPolicies
{
    public void AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(PolicyNames.CampAdminOrAdmin, policy =>
            policy.RequireRole(CampsRoles.CampsAdmin, RoleNames.Admin));

        // CampAdmin/Admin OR any team coordinator — the OR lives in
        // CampComplianceAccessHandler so the policy is a single requirement
        // (policy requirements AND together).
        options.AddPolicy(PolicyNames.CampComplianceAccess, policy =>
            policy.AddRequirements(new CampComplianceAccessRequirement()));
    }
}
