using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Consent;

/// <summary>
/// Consent's authorization policies, at the project root by convention. Discovered by Shell
/// alongside <see cref="Section"/> — nothing names it.
/// </summary>
internal sealed class SectionPolicies : ISectionPolicies
{
    public void AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(PolicyNames.ConsentCoordinatorBoardOrAdmin, policy =>
            policy.RequireRole(RoleNames.ConsentCoordinator, RoleNames.Board, RoleNames.Admin));
    }
}
