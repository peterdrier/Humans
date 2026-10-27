using Humans.Finance.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Finance;

internal sealed class SectionPolicies : ISectionPolicies
{
    public void AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(PolicyNames.FinanceAdminOrAdmin, policy =>
            policy.RequireRole(FinanceRoles.FinanceAdmin, RoleNames.Admin));
    }
}
