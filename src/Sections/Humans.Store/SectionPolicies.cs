using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Store;

internal sealed class SectionPolicies : ISectionPolicies
{
    public void AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(PolicyNames.StoreCatalogAdmin, policy =>
            policy.RequireRole(RoleNames.StoreAdmin, RoleNames.FinanceAdmin, RoleNames.Admin));
    }
}
