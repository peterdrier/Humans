using System.Security.Claims;
using Humans.Backdoor.Contracts;
using Humans.Backdoor.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Humans.Backdoor.Filters;

/// <summary>
/// Installs the key's owner (id and active roles) as the request principal. A missing header and an unknown or revoked
/// key are both 401, deliberately indistinguishable. Controllers carrying it are <c>[AllowAnonymous]</c>: the
/// authenticated-by-default fallback policy runs before MVC filters and would reject the request first.
/// </summary>
internal sealed class BackdoorApiKeyAuthFilter(
    IBackdoorApiKeyService keys) : IAsyncAuthorizationFilter
{
    public const string ApiKeyHeaderName = "X-Api-Key";

    public const string AuthenticationScheme = BackdoorAuthentication.SchemeName;

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
