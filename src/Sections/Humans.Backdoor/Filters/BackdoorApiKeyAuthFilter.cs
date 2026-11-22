using System.Security.Claims;
using Humans.Backdoor.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Humans.Backdoor.Filters;

/// <summary>
/// Installs the key's owner (id and active roles) as the request principal. A missing header and an unknown or revoked
/// key are both 401, deliberately indistinguishable. A good key whose holder the controller's
/// <see cref="BackdoorScopeAttribute"/> does not admit is 403, and so is a controller declaring no scope. Controllers carrying it are <c>[AllowAnonymous]</c>: the
/// authenticated-by-default fallback policy runs before MVC filters and would reject the request first. That same
/// attribute is what lets the key's principal, which carries no account-state claim, past the Shell's membership gates.
/// </summary>
internal sealed class BackdoorApiKeyAuthFilter(
    IBackdoorApiKeyService keys) : IAsyncAuthorizationFilter
{
    public const string ApiKeyHeaderName = "X-Api-Key";

    public const string AuthenticationScheme = "BackdoorApiKey";

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (!context.HttpContext.Request.Headers.TryGetValue(ApiKeyHeaderName, out var presented))
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        var owner = await keys.ResolveOwnerAsync(presented.ToString(), context.HttpContext.RequestAborted);
        if (owner is null)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        var scope = context.ActionDescriptor.EndpointMetadata.OfType<BackdoorScopeAttribute>().FirstOrDefault();
        if (scope is null || !await keys.AdmitsAsync(owner, scope.Scope, context.HttpContext.RequestAborted))
        {
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
            return;
        }

        // GetCurrentUserId() and the Serilog enricher read NameIdentifier. The role claims make IsInRole behave as for
        // a cookie-authed request, so a key never reads past its owner.
        Claim[] claims =
        [
            new Claim(ClaimTypes.NameIdentifier, owner.UserId.ToString()),
            .. owner.Roles.Select(a => new Claim(ClaimTypes.Role, a.RoleName))
        ];

        context.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationScheme));
    }
}
