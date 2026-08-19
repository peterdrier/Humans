using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Teams;

/// <summary>
/// Teams' authorization policies, at the project root by convention. Discovered by Shell
/// alongside <see cref="Section"/> — nothing names it.
/// </summary>
internal sealed class SectionPolicies : ISectionPolicies
{
    public void AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(PolicyNames.TeamsAdminBoardOrAdmin, policy =>
            policy.RequireRole(RoleNames.TeamsAdmin, RoleNames.Board, RoleNames.Admin));

        // TeamsAdmin or Admin — deliberately narrower than TeamsAdminBoardOrAdmin (Board is
        // not included) for gates where Board access would dead-end the viewer (e.g. the
        // Team page's "Open store" link, which Board members can't otherwise use).
        options.AddPolicy(PolicyNames.TeamsAdminOrAdmin, policy =>
            policy.RequireRole(RoleNames.TeamsAdmin, RoleNames.Admin));
    }
}
