using Humans.Rideshare.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Rideshare;

internal sealed class SectionPolicies : ISectionPolicies
{
    public void AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(PolicyNames.RideshareAdminOrAdmin, policy =>
            policy.RequireRole(RideshareRoles.RideshareAdmin, RoleNames.Admin));
    }
}
