using Humans.Events.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Events;

internal sealed class SectionPolicies : ISectionPolicies
{
    public void AddPolicies(AuthorizationOptions options)
    {
        options.AddPolicy(PolicyNames.EventsAdminOrAdmin, policy =>
            policy.RequireRole(EventsRoles.EventsAdmin, RoleNames.Admin));
    }
}
