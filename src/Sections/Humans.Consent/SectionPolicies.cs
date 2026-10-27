using Humans.Consent.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Consent;

internal sealed class SectionPolicies : ISectionPolicies
{
    public void AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(PolicyNames.ConsentCoordinatorBoardOrAdmin, policy =>
            policy.RequireRole(ConsentRoles.ConsentCoordinator, RoleNames.Board, RoleNames.Admin));
    }
}
