using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Cantina;

internal sealed class SectionPolicies : ISectionPolicies
{
    public void AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(PolicyNames.CantinaAdminOrAdmin, policy =>
            policy.RequireRole(RoleNames.CantinaAdmin, RoleNames.Admin));
    }
}
