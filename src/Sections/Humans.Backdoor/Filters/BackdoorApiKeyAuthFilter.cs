using Humans.Backdoor.Authentication;
using Humans.Backdoor.Services;
using Humans.Base.Extensions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Humans.Backdoor.Filters;

/// <summary>
/// Signs the request in through <see cref="BackdoorApiKeyHandler"/> and installs the key's owner as the request
/// principal. No key and a refused key are both 401. A good key whose owner the controller's
/// <see cref="BackdoorScopeAttribute"/> does not admit is 403, and so is a controller declaring no scope. Controllers carrying it are <c>[AllowAnonymous]</c>: the
/// authenticated-by-default fallback policy runs before MVC filters and would reject the request first. That same
/// attribute keeps the Shell's onboarding gates, which redirect to HTML pages, away from a JSON caller.
/// </summary>
internal sealed class BackdoorApiKeyAuthFilter(
    IBackdoorApiKeyService keys) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var result = await context.HttpContext.AuthenticateAsync(BackdoorApiKeyHandler.SchemeName);
        if (!result.Succeeded || result.Principal.GetUserId() is not { } userId)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        var scope = context.ActionDescriptor.EndpointMetadata.OfType<BackdoorScopeAttribute>().FirstOrDefault();
        if (scope is null || !await keys.AdmitsAsync(userId, scope.Scope, context.HttpContext.RequestAborted))
        {
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
            return;
        }

        context.HttpContext.User = result.Principal;
    }
}
