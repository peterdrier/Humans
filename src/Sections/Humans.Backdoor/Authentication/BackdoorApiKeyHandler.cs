using System.Security.Claims;
using System.Text.Encodings.Web;
using Humans.Backdoor.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Humans.Backdoor.Authentication;

/// <summary>
/// The one API key check, shared by the REST routes (through <c>BackdoorApiKeyAuthFilter</c>) and the MCP endpoint.
/// Never a default scheme: only callers that name it run it, so a key never signs anyone in to the browser pages.
/// </summary>
/// <remarks>
/// The principal carries only the owner's id. The Shell's claims transformation adds roles and account state, as for
/// any sign-in, so a key and a browser session for the same person look the same to every check after this one.
/// A missing header and an unknown, revoked or expired key all fail alike, deliberately indistinguishable.
/// </remarks>
internal sealed class BackdoorApiKeyHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IBackdoorApiKeyService keys)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "BackdoorApiKey";

    public const string HeaderName = "X-Api-Key";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var presented))
            return AuthenticateResult.NoResult();

        var owner = await keys.ResolveOwnerAsync(presented.ToString(), Context.RequestAborted);
        if (owner is null)
            return AuthenticateResult.Fail("API key refused.");

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, owner.UserId.ToString())], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
