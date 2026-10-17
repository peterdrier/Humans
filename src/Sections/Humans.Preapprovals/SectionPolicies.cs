using Humans.Base.Interfaces;
using Humans.Preapprovals.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Preapprovals;

/// <summary>Preapprovals' two policies, at the project root by convention. Discovered by Shell.</summary>
internal sealed class SectionPolicies : ISectionPolicies
{
    public void AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(PreapprovalsPolicies.Manage, policy =>
            policy.AddRequirements(new ManagesPreapprovalsRequirement()));
        options.AddPolicy(PreapprovalsPolicies.Access, policy =>
            policy.AddRequirements(new PreapprovalsAccessRequirement()));
    }
}
