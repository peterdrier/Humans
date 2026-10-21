using Humans.Base.Interfaces;
using Humans.Grants.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Grants;

/// <summary>Grants' policy, at the project root by convention. Discovered by Shell.</summary>
internal sealed class SectionPolicies : ISectionPolicies
{
    public void AddPolicies(AuthorizationOptions options) =>
        options.AddPolicy(GrantsPolicies.Manage, policy => policy.AddRequirements(new ManagesGrantsRequirement()));
}
