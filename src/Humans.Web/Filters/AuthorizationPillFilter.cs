using System.Reflection;
using Humans.Auth.Contracts;
using Humans.Base.Constants;
using Humans.Base.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Humans.Web.Filters;

/// <summary>
/// Sets ViewData["AuthPillRoles"] from the action's (or else the controller's) [Authorize] attributes, reading each
/// policy's registered requirements, so the layout renders the auth pill.
/// </summary>
public class AuthorizationPillFilter(IAuthorizationPolicyProvider policies, RoleCatalog catalog) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.Controller is Controller controller
            && context.HttpContext.User.Identity?.IsAuthenticated == true
            && context.ActionDescriptor is ControllerActionDescriptor descriptor
            && await PillTextAsync(descriptor) is { } text)
        {
            controller.ViewData["AuthPillRoles"] = text;
        }

        await next();
    }

    internal async Task<string?> PillTextAsync(ControllerActionDescriptor descriptor)
    {
        if (descriptor.MethodInfo.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any())
            return null;

        // Action-level [Authorize] OVERRIDES controller-level (narrower restriction wins) — otherwise pill misleads with the union.
        var attributes = descriptor.MethodInfo.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();
        if (attributes.Count == 0)
            attributes = descriptor.ControllerTypeInfo.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();

        // Every admin role is too long a list to read.
        if (attributes.Any(a => string.Equals(a.Policy, PolicyNames.AnyAdminRole, StringComparison.Ordinal)))
            return "Any admin role";

        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in attributes)
        {
            if (!string.IsNullOrEmpty(attribute.Roles))
                roles.UnionWith(attribute.Roles.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

            if (!string.IsNullOrEmpty(attribute.Policy) && await policies.GetPolicyAsync(attribute.Policy) is { } policy)
                roles.UnionWith(Admits(policy));
        }

        if (roles.Count == 0)
            return null;

        if (roles.Count == 1 && roles.Contains(RoleNames.Admin))
            return "Admin only";

        roles.Remove(RoleNames.Admin);
        return string.Join(" · ", roles.Select(catalog.Title).OrderBy(t => t, StringComparer.Ordinal));
    }

    /// <summary>Requirements that are neither a role check nor described (e.g. "is an active member") add nobody.</summary>
    internal static IEnumerable<string> Admits(AuthorizationPolicy policy) =>
        policy.Requirements.SelectMany(requirement => requirement switch
        {
            RolesAuthorizationRequirement roles => roles.AllowedRoles,
            IDescribedRequirement described => described.Admits,
            _ => [],
        });
}
