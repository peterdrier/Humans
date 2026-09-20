using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.CityPlanning.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Humans.CityPlanning;

/// <summary>
/// City Planning's authorization policies, at the project root by convention. Discovered by
/// Shell alongside <see cref="Section"/> — nothing names it. Registered from here, not from
/// Camps, so the policy leaves with its only consumer (this section's /Settings tab).
/// </summary>
internal sealed class SectionPolicies : ISectionPolicies
{
    public void AddPolicies(AuthorizationOptions options)
    {
        // CampAdmin/Admin OR a city-planning team member — the OR lives in
        // CityPlanningMapAdminHandler so the policy is a single requirement
        // (policy requirements AND together).
        options.AddPolicy(PolicyNames.CityPlanningMapAdmin, policy =>
            policy.AddRequirements(new CityPlanningMapAdminRequirement()));
    }
}
