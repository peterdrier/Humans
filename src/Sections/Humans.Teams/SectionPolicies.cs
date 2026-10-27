using Humans.Teams.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Teams;

internal sealed class SectionPolicies : ISectionPolicies
{
    public void AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(PolicyNames.TeamsAdminBoardOrAdmin, policy =>
            policy.RequireRole(TeamsRoles.TeamsAdmin, RoleNames.Board, RoleNames.Admin));

        // Narrower than TeamsAdminBoardOrAdmin: for gates where Board access would dead-end the viewer (e.g. the Team
        // page's "Open store" link).
        options.AddPolicy(PolicyNames.TeamsAdminOrAdmin, policy =>
            policy.RequireRole(TeamsRoles.TeamsAdmin, RoleNames.Admin));
    }
}
