using Humans.Users.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Humans.Users.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Users;

internal sealed class SectionPolicies : ISectionPolicies
{
    public void AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(PolicyNames.HumanAdminBoardOrAdmin, policy =>
            policy.RequireRole(UsersRoles.UsersAdmin, RoleNames.Board, RoleNames.Admin));

        options.AddPolicy(PolicyNames.HumanAdminOrAdmin, policy =>
            policy.RequireRole(UsersRoles.UsersAdmin, RoleNames.Admin));

        options.AddPolicy(PolicyNames.HumanAdminOnly, policy =>
            policy.AddRequirements(new HumanAdminOnlyRequirement()));
    }
}
