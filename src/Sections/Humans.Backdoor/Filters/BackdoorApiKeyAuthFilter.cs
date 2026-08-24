using System.Security.Claims;
using Humans.Backdoor.Contracts;
using Humans.Backdoor.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Humans.Backdoor.Filters;

/// <summary>
/// The one <c>X-Api-Key</c> gate on <c>/api/backdoor/*</c>. Resolves the presented key to
/// the human it was issued to and installs that human as the request principal, so every
/// read is attributable and every write records a real actor instead of <c>null</c>
/// (nobodies-collective/Humans#1128).
/// </summary>
/// <remarks>
/// 401 covers both a missing header and an unknown or revoked key — deliberately
/// indistinguishable to the caller. There is no 503 "not configured" case any more: keys are
/// rows an admin allocates, not an environment variable a deploy might forget, so an empty
/// table is an unauthorized caller rather than a misconfigured server.
/// </remarks>
internal sealed class BackdoorApiKeyAuthFilter(IBackdoorApiKeyService keys) : IAsyncAuthorizationFilter
{
    public const string ApiKeyHeaderName = "X-Api-Key";

    /// <summary>The <c>AuthenticationType</c> stamped on the identity a key produces.</summary>
    public const string AuthenticationScheme = BackdoorAuthentication.SchemeName;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (!context.HttpContext.Request.Headers.TryGetValue(ApiKeyHeaderName, out var presented))
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        var ownerUserId = await keys.ResolveOwnerAsync(presented.ToString(), context.HttpContext.RequestAborted);
        if (ownerUserId is null)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        // NameIdentifier is what ApiControllerBase.GetCurrentUserId() and the Serilog
        // CurrentUserEnricher both read, so attribution and log enrichment come for free.
        context.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, ownerUserId.Value.ToString())],
            AuthenticationScheme));
    }
}
