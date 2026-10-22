using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.CityPlanning.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Humans.CityPlanning;

/// <summary>Registered here, not from Camps, so the policy leaves with its only consumer.</summary>
internal sealed class SectionPolicies : ISectionPolicies
{
    public void AddPolicies(AuthorizationOptions options)
    {
        // The OR lives in CityPlanningMapAdminHandler, since policy requirements AND together.
        options.AddPolicy(PolicyNames.CityPlanningMapAdmin, policy =>
            policy.AddRequirements(new CityPlanningMapAdminRequirement()));
    }
}
